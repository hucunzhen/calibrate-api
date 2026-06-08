namespace CalibOperatorCLI_Example
{
    internal sealed class FlowRecipeOperationResult
    {
        public bool Success { get; private init; }
        public string? RecipeName { get; private init; }
        public string? Error { get; private init; }

        public static FlowRecipeOperationResult Ok(string recipeName) =>
            new() { Success = true, RecipeName = recipeName };

        public static FlowRecipeOperationResult Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
