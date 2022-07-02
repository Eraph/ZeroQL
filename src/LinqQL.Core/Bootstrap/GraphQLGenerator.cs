﻿using System;
using System.Collections.Generic;
using System.Linq;
using GraphQLParser;
using GraphQLParser.AST;
using LinqQL.Core.Extensions;
using LinqQL.Core.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using List = LinqQL.Core.Schema.List;
using TypeKind = LinqQL.Core.Schema.TypeKind;

namespace LinqQL.Core.Bootstrap;

public static class GraphQLGenerator
{

    public static string ToCSharp(string graphql, string clientNamespace)
    {
        var schema = Parser.Parse(graphql);
        var enums = schema.Definitions
            .OfType<GraphQLEnumTypeDefinition>()
            .ToArray();

        var enumsNames = new HashSet<string>(enums.Select(o => o.Name.StringValue));

        var context = new TypeFormatter(enumsNames);
        var inputs = schema.Definitions
            .OfType<GraphQLInputObjectTypeDefinition>()
            .Select(o => CreateInputDefinition(context, o))
            .ToArray();

        var types = schema.Definitions
            .OfType<GraphQLObjectTypeDefinition>()
            .Select(o => CreateTypesDefinition(context, o))
            .ToArray();


        var namespaceDeclaration = NamespaceDeclaration(IdentifierName(clientNamespace));
        var typesDeclaration = GenerateTypes(types.Concat(inputs).ToArray());
        var enumsDeclaration = GenerateEnums(enums);

        namespaceDeclaration = namespaceDeclaration
            .WithMembers(List<MemberDeclarationSyntax>(typesDeclaration).AddRange(enumsDeclaration));

        var formattedSource = namespaceDeclaration.NormalizeWhitespace().ToFullString();
        return $@"// This file generated for LinqQL.
// <auto-generated/>
using System; 
using System.Linq; 
using System.Text.Json.Serialization; 

{formattedSource}";
    }

    private static ClassDefinition CreateInputDefinition(TypeFormatter typeFormatter, GraphQLInputObjectTypeDefinition input)
    {
        var typeDefinition = new ClassDefinition
        {
            Name = input.Name.StringValue,
            Properties = CretePropertyDefinition(typeFormatter, input)
        };

        return typeDefinition;
    }

    private static EnumDeclarationSyntax[] GenerateEnums(GraphQLEnumTypeDefinition[] enums)
    {
        return enums.Select(e =>
            {
                var members = e.Values.Select(o =>
                    {
                        var name = o.Name.StringValue;
                        return EnumMemberDeclaration(Identifier(name));
                    })
                    .ToArray();

                var enumSyntax = EnumDeclaration(Identifier(e.Name.StringValue))
                    .AddAttributeLists(AttributeList()
                        .AddAttributes(Attribute(ParseName(SourceGeneratorInfo.CodeGenerationAttribute))))
                    .AddMembers(members)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword));

                return enumSyntax;
            })
            .ToArray();
    }

    private static ClassDeclarationSyntax[] GenerateTypes(ClassDefinition[] classes)
    {
        return classes
            .Select(o =>
            {
                var backedFields = o.Properties
                    .Where(property => property.TypeKind is Complex or List)
                    .Select(property =>
                    {
                        var jsonNameAttributes =
                            AttributeList()
                                .AddAttributes(
                                    Attribute(IdentifierName("global::System.ComponentModel.EditorBrowsable"))
                                        .AddArgumentListArguments(
                                            AttributeArgument(
                                                ParseExpression("global::System.ComponentModel.EditorBrowsableState.Never"))),
                                    Attribute(IdentifierName("JsonPropertyName"))
                                        .AddArgumentListArguments(
                                            AttributeArgument(
                                                LiteralExpression(
                                                    SyntaxKind.StringLiteralExpression,
                                                    Literal(property.Name))))
                                );

                        return PropertyDeclaration(
                                ParseTypeName(property.TypeName),
                                Identifier("__" + property.Name))
                            .AddModifiers(Token(SyntaxKind.PublicKeyword))
                            .AddAccessorListAccessors(
                                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(ParseToken(";")),
                                AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                                    .WithSemicolonToken(ParseToken(";")))
                            .AddAttributeLists(jsonNameAttributes);
                    });

                var fields = o.Properties.Select(GeneratePropertiesDeclarations);
                return ClassDeclaration(o.Name)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .AddAttributeLists(AttributeList()
                        .AddAttributes(Attribute(ParseName(SourceGeneratorInfo.CodeGenerationAttribute))))
                    .WithMembers(List<MemberDeclarationSyntax>(backedFields).AddRange(fields));

            })
            .ToArray();
    }

    private static MemberDeclarationSyntax GeneratePropertiesDeclarations(FieldDefinition field)
    {
        if (field.TypeKind is Complex or List)
        {
            var parameters = field.Arguments
                .Select(o =>
                    Parameter(Identifier(o.Name))
                        .WithType(ParseTypeName(o.TypeName)))
                .ToArray();

            if (field.TypeKind == TypeKind.Complex)
            {
                return GenerateObjectProperty(field, parameters);
            }

            if (field.TypeKind is List list)
            {
                return GenerateListPropertyDeclaration(field, list, parameters);
            }
        }

        return PropertyDeclaration(ParseTypeName(field.TypeName), Identifier(field.Name))
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(ParseToken(";")),
                AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(ParseToken(";")));
    }

    private static MemberDeclarationSyntax GenerateListPropertyDeclaration(FieldDefinition field, List list, ParameterSyntax[] parameters)
    {
        if (list.ElementTypeKind is Complex)
        {
            var elementType = GetElementTypeFromArray(field);
            var selectorParameter = Parameter(Identifier("selector"))
                .WithType(ParseTypeName($"Func<{elementType}, T>"));

            var genericMethodWithType = MethodDeclaration(
                    IdentifierName("T[]"),
                    Identifier(field.Name + "<T>"))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithParameterList(
                    ParameterList(
                        SeparatedList(parameters)
                            .Add(selectorParameter)));

            var body = Block(
                ParseStatement($"return __{field.Name}.Select(selector).ToArray();"));

            return genericMethodWithType
                .WithBody(body);
        }
        
        if (list.ElementTypeKind is Scalar)
        {
            var elementType = GetElementTypeFromArray(field);
            var genericMethodWithType = MethodDeclaration(
                    IdentifierName($"{elementType}[]"),
                    Identifier(field.Name))
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .WithParameterList(
                    ParameterList(SeparatedList(parameters)));

            var body = Block(
                ParseStatement($"return __{field.Name};"));

            return genericMethodWithType
                .WithBody(body);
        }

        throw new NotSupportedException($"Not supported type: {list.ElementTypeKind}");
    }

    private static MemberDeclarationSyntax GenerateObjectProperty(FieldDefinition field, ParameterSyntax[] parameters)
    {

        var selectorParameter = Parameter(Identifier("selector"))
            .WithType(ParseTypeName($"Func<{field.TypeName}, T>"));

        var genericMethodWithType = MethodDeclaration(
                IdentifierName("T"),
                Identifier(field.Name + "<T>"))
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .WithParameterList(
                ParameterList(
                    SeparatedList(parameters)
                        .Add(selectorParameter)));

        var body = Block(
            ReturnStatement(
                InvocationExpression(IdentifierName("selector"))
                    .AddArgumentListArguments(Argument(IdentifierName("__" + field.Name)))));

        return genericMethodWithType
            .WithBody(body);
    }

    private static string GetElementTypeFromArray(FieldDefinition field)
    {
        if (field.TypeName.EndsWith("?"))
        {
            return field.TypeName[..^3];
        }
        return field.TypeName[..^2];
    }

    private static ClassDefinition CreateTypesDefinition(TypeFormatter typeFormatter, GraphQLObjectTypeDefinition type)
    {
        var typeDefinition = new ClassDefinition
        {
            Name = type.Name.StringValue,
            Properties = CretePropertyDefinition(typeFormatter, type)
        };

        return typeDefinition;
    }

    private static FieldDefinition[] CretePropertyDefinition(TypeFormatter typeFormatter, GraphQLInputObjectTypeDefinition typeQL)
    {
        return typeQL.Fields?.Select(field =>
            {
                var type = typeFormatter.GetTypeDefinition(field.Type);
                return new FieldDefinition
                {
                    Name = field.Name.StringValue.FirstToUpper(),
                    TypeName = type.Name,
                    TypeKind = type.TypeKind,
                    Arguments = Array.Empty<ArgumentDefinition>()
                };
            })
            .ToArray() ?? Array.Empty<FieldDefinition>();
    }

    private static FieldDefinition[] CretePropertyDefinition(TypeFormatter typeFormatter, GraphQLObjectTypeDefinition typeQL)
    {
        return typeQL.Fields?.Select(field =>
            {
                var type = typeFormatter.GetTypeDefinition(field.Type);
                return new FieldDefinition
                {
                    Name = field.Name.StringValue.FirstToUpper(),
                    TypeName = type.Name,
                    TypeKind = type.TypeKind,
                    Arguments = field.Arguments?
                        .Select(arg => new ArgumentDefinition { Name = arg.Name.StringValue, TypeName = typeFormatter.GetTypeDefinition(arg.Type).Name })
                        .ToArray() ?? Array.Empty<ArgumentDefinition>()
                };
            })
            .ToArray() ?? Array.Empty<FieldDefinition>();
    }
}