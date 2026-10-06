using XStatePort.Examples;

if (args.Length != 1 || !WorkflowExamples.Names.Contains(args[0], StringComparer.Ordinal))
{
    Console.Error.WriteLine("Choose a ported example: " + string.Join(", ", WorkflowExamples.Names));
    return 2;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var input = new StreamReader(Console.OpenStandardInput());
async Task<string> Prompt(string _) => await input.ReadLineAsync(timeout.Token).ConfigureAwait(false)
    ?? throw new EndOfStreamException("Prompt input closed.");
await WorkflowExecution.RunAsync(args[0], Console.WriteLine, cancellationToken: timeout.Token, prompt: Prompt).ConfigureAwait(false);
return 0;
