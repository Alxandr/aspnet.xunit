using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.TestAdapter;
using Xunit.Abstractions;
using VsTestCase = Microsoft.Framework.TestAdapter.Test;
using System.Threading.Tasks;
using xunit.runner.aspnet.Utility;

namespace Xunit.Runner.AspNet
{
    public class Program
    {
#pragma warning disable 0649
        volatile bool cancel;
#pragma warning restore 0649
        bool failed;
        readonly ConcurrentDictionary<string, ExecutionSummary> completionMessages = new ConcurrentDictionary<string, ExecutionSummary>();
        readonly ConcurrentDictionary<string, Type> visitorTypes = new ConcurrentDictionary<string, Type>();

        private readonly IApplicationEnvironment _appEnv;
        private readonly IServiceProvider _services;
        private readonly VisitorService _visitorService;

        public Program(IApplicationEnvironment appEnv, IServiceProvider services, ILibraryManager libraryManager)
        {
            _appEnv = appEnv;
            _services = services;
            _visitorService = new VisitorService(libraryManager);
        }

        [STAThread]
        public int Main(string[] args)
        {
            args = Enumerable.Repeat(_appEnv.ApplicationName + ".dll", 1).Concat(args).ToArray();

            var originalForegroundColor = Console.ForegroundColor;

            try
            {
                var framework = _appEnv.RuntimeFramework;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("xUnit.net ASP.NET test runner ({0}-bit {1} {2})", IntPtr.Size * 8, framework.Identifier, framework.Version);
                Console.WriteLine("Copyright (C) 2015 Outercurve Foundation.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Gray;

                if (args.Length == 0 || args[0] == "-?")
                {
                    PrintUsage();
                    return 1;
                }

#if !ASPNETCORE50
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Console.CancelKeyPress += (sender, e) =>
                {
                    if (!cancel)
                    {
                        Console.WriteLine("Canceling... (Press Ctrl+C again to terminate)");
                        cancel = true;
                        e.Cancel = true;
                    }
                };
#endif

                var defaultDirectory = Directory.GetCurrentDirectory();
                if (!defaultDirectory.EndsWith(new String(new[] { Path.DirectorySeparatorChar })))
                    defaultDirectory += Path.DirectorySeparatorChar;

                var commandLine = CommandLine.Parse(args);

                var failCount = RunProject(defaultDirectory, commandLine.Project, commandLine.Visitor,
                                           commandLine.ParallelizeAssemblies, commandLine.ParallelizeTestCollections,
                                           commandLine.MaxParallelThreads,
                                           commandLine.DesignTime, commandLine.List, commandLine.DesignTimeTestUniqueNames);

                if (commandLine.Wait)
                {
                    Console.WriteLine();

                    Console.Write("Press ENTER to continue...");
                    Console.ReadLine();

                    Console.WriteLine();
                }

                return failCount;
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("error: {0}", ex.Message);
                return 1;
            }
            catch (BadImageFormatException ex)
            {
                Console.WriteLine("{0}", ex.Message);
                return 1;
            }
            finally
            {
                Console.ForegroundColor = originalForegroundColor;
            }
        }

#if !ASPNETCORE50
        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;

            if (ex != null)
                Console.WriteLine(ex.ToString());
            else
                Console.WriteLine("Error of unknown type thrown in application domain");

            Environment.Exit(1);
        }
#endif

        static void PrintUsage()
        {
            Console.WriteLine("usage: xunit.runner.aspnet <assemblyFile> [assemblyFile...] [options]");
            Console.WriteLine();
            Console.WriteLine("Valid options:");
            Console.WriteLine("  -parallel option         : set parallelization based on option");
            Console.WriteLine("                           :   none - turn off all parallelization");
            Console.WriteLine("                           :   collections - only parallelize collections");
            Console.WriteLine("                           :   assemblies - only parallelize assemblies");
            Console.WriteLine("                           :   all - parallelize collections and assemblies");
            Console.WriteLine("  -maxthreads count        : maximum thread count for collection parallelization");
            Console.WriteLine("                           :   0 - run with unbounded thread count");
            Console.WriteLine("                           :   >0 - limit task thread pool size to 'count'");
            Console.WriteLine("  -noshadow                : do not shadow copy assemblies");
            Console.WriteLine("  -visitor name            : the visitor to use to generate the output");
            Console.WriteLine("  -wait                    : wait for input after completion");
            Console.WriteLine("  -trait \"name=value\"    : only run tests with matching name/value traits");
            Console.WriteLine("                           : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -notrait \"name=value\"  : do not run tests with matching name/value traits");
            Console.WriteLine("                           : if specified more than once, acts as an AND operation");
            Console.WriteLine("  -method \"name\"         : run a given test method (should be fully specified;");
            Console.WriteLine("                           : i.e., 'MyNamespace.MyClass.MyTestMethod')");
            Console.WriteLine("                           : if specified more than once, acts as an OR operation");
            Console.WriteLine("  -class \"name\"          : run all methods in a given test class (should be fully");
            Console.WriteLine("                           : specified; i.e., 'MyNamespace.MyClass')");
            Console.WriteLine("                           : if specified more than once, acts as an OR operation");

            foreach (var transform in TransformFactory.AvailableTransforms)
                Console.WriteLine("  {0} : {1}",
                                  String.Format("-{0} <filename>", transform.CommandLine).PadRight(22).Substring(0, 22),
                                  transform.Description);
        }

        int RunProject(string defaultDirectory, XunitProject project, string visitor, bool? parallelizeAssemblies, bool? parallelizeTestCollections, int? maxThreadCount, bool designTime, bool list, IReadOnlyList<string> designTimeFullyQualifiedNames)
        {
            XElement assembliesElement = null;
            var xmlTransformers = TransformFactory.GetXmlTransformers(project);
            var needsXml = xmlTransformers.Count > 0;
            var consoleLock = new object();

            if (!parallelizeAssemblies.HasValue)
                parallelizeAssemblies = project.All(assembly => assembly.Configuration.ParallelizeAssembly);

            if (needsXml)
                assembliesElement = new XElement("assemblies");

            var originalWorkingFolder = Directory.GetCurrentDirectory();

            using (AssemblyHelper.SubscribeResolve())
            {
                var clockTime = Stopwatch.StartNew();

                if (parallelizeAssemblies.GetValueOrDefault())
                {
                    var tasks = project.Assemblies.Select(assembly => Task.Run(() => ExecuteAssembly(consoleLock, defaultDirectory, assembly, needsXml, visitor, parallelizeTestCollections, maxThreadCount, project.Filters, designTime, list, designTimeFullyQualifiedNames)));
                    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                    foreach (var assemblyElement in results.Where(result => result != null))
                        assembliesElement.Add(assemblyElement);
                }
                else
                {
                    foreach (var assembly in project.Assemblies)
                    {
                        var assemblyElement = ExecuteAssembly(consoleLock, defaultDirectory, assembly, needsXml, visitor, parallelizeTestCollections, maxThreadCount, project.Filters, designTime, list, designTimeFullyQualifiedNames);
                        if (assemblyElement != null)
                            assembliesElement.Add(assemblyElement);
                    }
                }

                clockTime.Stop();

                if (completionMessages.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine();
                    Console.WriteLine("=== TEST EXECUTION SUMMARY ===");
                    Console.ForegroundColor = ConsoleColor.Gray;

                    var totalTestsRun = completionMessages.Values.Sum(summary => summary.Total);
                    var totalTestsFailed = completionMessages.Values.Sum(summary => summary.Failed);
                    var totalTestsSkipped = completionMessages.Values.Sum(summary => summary.Skipped);
                    var totalTime = completionMessages.Values.Sum(summary => summary.Time).ToString("0.000s");
                    var totalErrors = completionMessages.Values.Sum(summary => summary.Errors);
                    var longestAssemblyName = completionMessages.Keys.Max(key => key.Length);
                    var longestTotal = totalTestsRun.ToString().Length;
                    var longestFailed = totalTestsFailed.ToString().Length;
                    var longestSkipped = totalTestsSkipped.ToString().Length;
                    var longestTime = totalTime.Length;
                    var longestErrors = totalErrors.ToString().Length;

                    foreach (var message in completionMessages.OrderBy(m => m.Key))
                        Console.WriteLine("   {0}  Total: {1}, Errors: {2}, Failed: {3}, Skipped: {4}, Time: {5}",
                                          message.Key.PadRight(longestAssemblyName),
                                          message.Value.Total.ToString().PadLeft(longestTotal),
                                          message.Value.Errors.ToString().PadLeft(longestErrors),
                                          message.Value.Failed.ToString().PadLeft(longestFailed),
                                          message.Value.Skipped.ToString().PadLeft(longestSkipped),
                                          message.Value.Time.ToString("0.000s").PadLeft(longestTime));

                    if (completionMessages.Count > 1)
                        Console.WriteLine("   {0}         {1}          {2}          {3}           {4}        {5}" + Environment.NewLine +
                                          "           {6} {7}          {8}          {9}           {10}        {11} ({12})",
                                          " ".PadRight(longestAssemblyName),
                                          "-".PadRight(longestTotal, '-'),
                                          "-".PadRight(longestErrors, '-'),
                                          "-".PadRight(longestFailed, '-'),
                                          "-".PadRight(longestSkipped, '-'),
                                          "-".PadRight(longestTime, '-'),
                                          "GRAND TOTAL:".PadLeft(longestAssemblyName),
                                          totalTestsRun,
                                          totalErrors,
                                          totalTestsFailed,
                                          totalTestsSkipped,
                                          totalTime,
                                          clockTime.Elapsed.TotalSeconds.ToString("0.000s"));

                }
            }

            Directory.SetCurrentDirectory(originalWorkingFolder);

            foreach (var transformer in xmlTransformers)
                transformer(assembliesElement);

            return failed ? 1 : completionMessages.Values.Sum(summary => summary.Failed);
        }

        TestMessageVisitor<ITestAssemblyFinished> CreateVisitor(object consoleLock, string defaultDirectory, XElement assemblyElement, string visitor)
        {
            Type visitorType;
            if (visitor != null)
            {
                visitorType = _visitorService.GetVisitorType(visitor);
                if (visitorType == null)
                    throw new ArgumentException("Visitor with name " + visitor + " was not found");
            }
            else
            {
                visitorType = _visitorService.Recomended;
            }

            if(visitorType == null)
                return new StandardOutputVisitor(consoleLock, defaultDirectory, assemblyElement, () => cancel, completionMessages);

            return (TestMessageVisitor<ITestAssemblyFinished>) Activator.CreateInstance(visitorType, assemblyElement, new Func<bool>(() => cancel));
        }

        XElement ExecuteAssembly(object consoleLock,
                                 string defaultDirectory,
                                 XunitProjectAssembly assembly,
                                 bool needsXml,
                                 string visitor,
                                 bool? parallelizeTestCollections,
                                 int? maxThreadCount,
                                 XunitFilters filters,
                                 bool designTime,
                                 bool listTestCases,
                                 IReadOnlyList<string> designTimeFullyQualifiedNames)
        {
            if (cancel)
                return null;

            var assemblyElement = needsXml ? new XElement("assembly") : null;

            try
            {
                var discoveryOptions = new XunitDiscoveryOptions(assembly.Configuration);
                var executionOptions = new XunitExecutionOptions(assembly.Configuration);
                if (maxThreadCount.HasValue)
                    executionOptions.MaxParallelThreads = maxThreadCount.GetValueOrDefault();
                if (parallelizeTestCollections.HasValue)
                    executionOptions.DisableParallelization = !parallelizeTestCollections.GetValueOrDefault();

                lock (consoleLock)
                {
                    if (assembly.Configuration.DiagnosticMessages)
                        Console.WriteLine("Discovering: {0} (method display = {1}, parallel test collections = {2}, max threads = {3})",
                                          Path.GetFileNameWithoutExtension(assembly.AssemblyFilename),
                                          discoveryOptions.MethodDisplay,
                                          !executionOptions.DisableParallelization,
                                          executionOptions.MaxParallelThreads);
                    else
                        Console.WriteLine("Discovering: {0}", Path.GetFileNameWithoutExtension(assembly.AssemblyFilename));
                }

                using (var controller = new XunitFrontController(assembly.AssemblyFilename, assembly.ConfigFilename, assembly.ShadowCopy))
                using (var discoveryVisitor = new TestDiscoveryVisitor())
                {
                    controller.Find(includeSourceInformation: false, messageSink: discoveryVisitor, discoveryOptions: discoveryOptions);
                    discoveryVisitor.Finished.WaitOne();

                    IDictionary<ITestCase, VsTestCase> vsTestcases = null;
                    if (designTime)
                        vsTestcases = DesignTimeTestConverter.Convert(discoveryVisitor.TestCases);

                    lock (consoleLock)
                        Console.WriteLine("Discovered:  {0}", Path.GetFileNameWithoutExtension(assembly.AssemblyFilename));

                    if (listTestCases)
                    {
                        lock (consoleLock)
                        {
                            if (designTime)
                            {
                                var sink = (ITestDiscoverySink)_services.GetService(typeof(ITestDiscoverySink));

                                foreach (var testcase in vsTestcases.Values)
                                {
                                    if (sink != null)
                                        sink.SendTest(testcase);

                                    Console.WriteLine(testcase.FullyQualifiedName);
                                }
                            }
                            else
                            {
                                foreach (var testcase in discoveryVisitor.TestCases)
                                    Console.WriteLine(testcase.DisplayName);
                            }
                        }

                        return assemblyElement;
                    }

                    var resultsVisitor = CreateVisitor(consoleLock, defaultDirectory, assemblyElement, visitor);

                    if (designTime)
                    {
                        var sink = (ITestExecutionSink)_services.GetService(typeof(ITestExecutionSink));
                        resultsVisitor = new DesignTimeExecutionVisitor(
                            sink,
                            vsTestcases,
                            resultsVisitor);
                    }

                    IList<ITestCase> filteredTestCases;
                    if (!designTime || designTimeFullyQualifiedNames.Count == 0)
                    {
                        filteredTestCases = discoveryVisitor.TestCases.Where(filters.Filter).ToList();
                    }
                    else
                    {
                        filteredTestCases = (from t in vsTestcases
                                             where designTimeFullyQualifiedNames.Contains(t.Value.FullyQualifiedName)
                                             select t.Key)
                                            .ToList();
                    }

                    if (filteredTestCases.Count == 0)
                    {
                        lock (consoleLock)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("ERROR:       {0} has no tests to run", Path.GetFileNameWithoutExtension(assembly.AssemblyFilename));
                            Console.ForegroundColor = ConsoleColor.Gray;
                        }
                    }
                    else
                    {
                        controller.RunTests(filteredTestCases, resultsVisitor, executionOptions);
                        resultsVisitor.Finished.WaitOne();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("{0}: {1}", ex.GetType().FullName, ex.Message);
                failed = true;
            }

            return assemblyElement;
        }
    }
}
