namespace Lua.CodeAnalysis.Syntax.Nodes;

public record FunctionDeclarationStatementNode(ReadOnlyMemory<char> Name, IdentifierNode[] ParameterNodes, SyntaxNode[] Nodes, bool HasVariableArguments, SourcePosition Position,int LineDefined,int LastLineDefined) : StatementNode(Position)
{
    public override TResult Accept<TContext, TResult>(ISyntaxNodeVisitor<TContext, TResult> visitor, TContext context)
    {
        return visitor.VisitFunctionDeclarationStatementNode(this, context);
    }
}