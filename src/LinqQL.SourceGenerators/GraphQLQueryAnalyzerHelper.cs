using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LinqQL.SourceGenerators
{
    public class GraphQLQueryAnalyzerHelper
    {
        public static IMethodSymbol? ExtractQueryMethod(Compilation compilation, InvocationExpressionSyntax invocation)
        {
            var semanticModel = compilation.GetSemanticModel(invocation.SyntaxTree);
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return null;
            }
            var possibleMethod = semanticModel.GetSymbolInfo(memberAccess.Name);
            if (possibleMethod.Symbol is not IMethodSymbol { ContainingSymbol: INamedTypeSymbol containingType } method ||
                containingType.ConstructedFrom.ToString() != "LinqQL.Core.GraphQLClient<TQuery>")
            {
                return null;
            }

            return method;
        }

        public static bool IsOpenLambda(SimpleLambdaExpressionSyntax lambda)
        {
            return lambda.Parameter.Identifier.ValueText == lambda.Body.ToString();
        }
    }
}