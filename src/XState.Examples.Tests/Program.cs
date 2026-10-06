if (args.Length != 1) throw new ArgumentException("Expected the example result output path.");
return await XStatePort.Examples.Tests.ExampleTests.RunAsync(args[0]).ConfigureAwait(false);
