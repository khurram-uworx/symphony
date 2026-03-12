namespace Symphony.App.Workflows;

public sealed record WorkflowDefinition(
    IReadOnlyDictionary<string, object> Config,
    string PromptTemplate);

public sealed class WorkflowException : Exception
{
    public WorkflowException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}
