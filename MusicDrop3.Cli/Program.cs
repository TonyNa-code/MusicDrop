using System.Reflection;

try
{
    Assembly app = typeof(MFlacDrop.Program).Assembly;
    Type cli = app.GetType("MFlacDrop.CliMode", throwOnError: true)!;
    MethodInfo run = cli.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(cli.FullName, "RunAsync");
    Task<int> task = (Task<int>)(run.Invoke(null, new object[] { args })
        ?? throw new InvalidOperationException("CLI entry point returned null."));
    return await task;
}
catch (TargetInvocationException ex) when (ex.InnerException is not null)
{
    Console.Error.WriteLine(ex.InnerException);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
