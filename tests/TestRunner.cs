using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

internal static class TestRunner
{
    public static async Task<int> Main()
    {
        var suites = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t.Name.EndsWith("Tests", StringComparison.Ordinal))
            .Select(t => t.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m != null).OrderBy(m => m.DeclaringType.Name).ToArray();
        try
        {
            foreach (var suite in suites)
            {
                Console.WriteLine("Running " + suite.DeclaringType.Name);
                var parameters = suite.GetParameters();
                object[] arguments = parameters.Length == 0 ? null : new object[] { new[] { Path.Combine(AppContext.BaseDirectory, "models.json") } };
                object result = suite.Invoke(null, arguments);
                if (result is Task<int> asynchronousResult) { if (await asynchronousResult != 0) return 1; }
                else if (result is Task asynchronous) await asynchronous;
                else if (result is int exit && exit != 0) return exit;
            }
            Console.WriteLine(suites.Length + " test suites passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is TargetInvocationException wrapper ? wrapper.InnerException : ex);
            return 1;
        }
    }
}
