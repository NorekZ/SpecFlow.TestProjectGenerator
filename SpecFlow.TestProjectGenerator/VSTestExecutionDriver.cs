﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using TechTalk.SpecFlow.TestProjectGenerator.Data;
using TechTalk.SpecFlow.TestProjectGenerator.Driver;
using TechTalk.SpecFlow.TestProjectGenerator.Helpers;

namespace TechTalk.SpecFlow.TestProjectGenerator
{
    public class VSTestExecutionDriver
    {
        private readonly VisualStudioFinder _visualStudioFinder;
        private readonly AppConfigDriver _appConfigDriver;
        private readonly TestProjectFolders _testProjectFolders;
        private readonly IOutputWriter _outputWriter;
        private readonly TestRunConfiguration _testRunConfiguration;
        private readonly TestSuiteInitializationDriver _testSuiteInitializationDriver;
        private UriCleaner _uriCleaner;

        private const string BeginnOfTrxFileLine = "Results File: ";
        private const string BeginnOfLogFileLine = "Log file: ";

        public VSTestExecutionDriver(VisualStudioFinder visualStudioFinder, AppConfigDriver appConfigDriver, TestProjectFolders testProjectFolders, IOutputWriter outputWriter, TestRunConfiguration testRunConfiguration, TestSuiteInitializationDriver testSuiteInitializationDriver)
        {
            _visualStudioFinder = visualStudioFinder;
            _appConfigDriver = appConfigDriver;
            _testProjectFolders = testProjectFolders;
            _outputWriter = outputWriter;
            _testRunConfiguration = testRunConfiguration;
            _testSuiteInitializationDriver = testSuiteInitializationDriver;
            _uriCleaner = new UriCleaner();
        }

        public TestExecutionResult LastTestExecutionResult { get; private set; }
        public string RunSettingsFile { get; set; }
        public string Filter { get; set; }

        public void CheckIsBindingMethodExecuted(string methodName, int timesExecuted)
        {
            string pathToLogFile = Path.Combine(_testProjectFolders.PathToSolutionDirectory, "steps.log");
            string logFileContent = File.ReadAllText(pathToLogFile, Encoding.UTF8);

            var regex = new Regex($@"-> step: {methodName}");

            regex.Match(logFileContent).Success.Should().BeTrue($"method {methodName} was not executed.");
            regex.Matches(logFileContent).Count.Should().Be(timesExecuted);
        }

        public void CheckOutputContainsText(string text)
        {
            LastTestExecutionResult.Output.Should().NotBeNull()
                                   .And.Subject.Should().Contain(text);
        }

        public void CheckAnyOutputContainsText(string text)
        {
            bool trxContainsEntry = LastTestExecutionResult.TrxOutput.Contains(text);
            bool outputContainsEntry = LastTestExecutionResult.Output.Contains(text);
            bool containsAtAll = trxContainsEntry || outputContainsEntry;
            containsAtAll.Should().BeTrue($"either Trx output or program output should contain '{text}'");
        }

        public TestExecutionResult ExecuteTests()
        {
            var task = ExecuteTestsInternalAsync(async (processHelper, parameters) =>
                processHelper.RunProcess(_outputWriter, _testProjectFolders.ProjectFolder, parameters.executablePath, parameters.argumentsFormat, parameters.environmentVariables));

            return task.Result;
        }

        public async Task<TestExecutionResult> ExecuteTestsAsync()
        {
            return await ExecuteTestsInternalAsync(async (processHelper, parameters) =>
                await processHelper.RunProcessAsync(_outputWriter, _testProjectFolders.ProjectFolder, parameters.executablePath, parameters.argumentsFormat, parameters.environmentVariables));
        }

        private async Task<TestExecutionResult> ExecuteTestsInternalAsync(Func<ProcessHelper, (string executablePath, string argumentsFormat, IReadOnlyDictionary<string, string> environmentVariables), Task<ProcessResult>> runProcessAction)
        {
            string vsFolder = _visualStudioFinder.Find();
            vsFolder = Path.Combine(vsFolder, _appConfigDriver.VSTestPath);

            string vsTestConsoleExePath = Path.Combine(AssemblyFolderHelper.GetAssemblyFolder(), Environment.ExpandEnvironmentVariables(vsFolder + @"\vstest.console.exe"));

            var envVariables = new Dictionary<string, string>
            {
                {"DOTNET_CLI_UI_LANGUAGE", "en"}
            };

            if (_testSuiteInitializationDriver.OverrideTestSuiteStartupTime is DateTime testRunStartupTime)
            {
                envVariables.Add("SpecFlow_Messages_TestRunStartedTimeOverride", $"{testRunStartupTime:O}");
            }

            if (_testSuiteInitializationDriver.OverrideTestCaseStartedPickleId is Guid startedPickleId)
            {
                envVariables.Add("SpecFlow_Messages_TestCaseStartedPickleIdOverride", $"{startedPickleId:D}");
            }

            if (_testSuiteInitializationDriver.OverrideTestCaseStartedTime is DateTime testCaseStartupTime)
            {
                envVariables.Add("SpecFlow_Messages_TestCaseStartedTimeOverride", $"{testCaseStartupTime:O}");
            }

            if (_testSuiteInitializationDriver.OverrideTestCaseFinishedPickleId is Guid finishedPickleId)
            {
                envVariables.Add("SpecFlow_Messages_TestCaseFinishedPickleIdOverride", $"{finishedPickleId:D}");
            }

            if (_testSuiteInitializationDriver.OverrideTestCaseFinishedTime is DateTime testCaseFinishedTime)
            {
                envVariables.Add("SpecFlow_Messages_TestCaseFinishedTimeOverride", $"{testCaseFinishedTime:O}");
            }

            var processHelper = new ProcessHelper();
            string arguments = GenereateVsTestsArguments();
            ProcessResult processResult;
            try
            {
                processResult = await runProcessAction(processHelper, (vsTestConsoleExePath, arguments, envVariables));
            }
            catch (Exception)
            {
                Console.WriteLine($"running vstest.console.exe failed - {_testProjectFolders.CompiledAssemblyPath} {vsTestConsoleExePath} {arguments}");
                throw;
            }

            string output = processResult.CombinedOutput;

            var lines = output.SplitByString(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var trxFiles = FindFilePath(lines, ".trx", BeginnOfTrxFileLine).ToArray();
            var logFiles = FindFilePath(lines, ".log", BeginnOfLogFileLine).ToArray();



            string logFileContent =
                logFiles.Length == 1
                ? File.ReadAllText(_uriCleaner.ConvertSlashes(_uriCleaner.StripSchema(Uri.UnescapeDataString(logFiles.Single()))))
                : string.Empty;

            var reportFiles = GetReportFiles(output);

            trxFiles.Should().HaveCount(1, $"exactly one TRX file should be generated by VsTest;{Environment.NewLine}{string.Join(Environment.NewLine, trxFiles)}");
            string trxFile = trxFiles.Single();

            var testResultDocument = XDocument.Load(trxFile);

            LastTestExecutionResult = CalculateTestExecutionResultFromTrx(testResultDocument, _testRunConfiguration, output, reportFiles, logFileContent);
            return LastTestExecutionResult;
        }

        private IEnumerable<string> GetReportFiles(string output)
        {
            const string reportFileString = @"Report file: ";

            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(i => i.StartsWith(reportFileString))
                         .Select(i => i.Substring(reportFileString.Length))
                         .Select(i => new Uri(i).AbsolutePath);
        }

        private TestExecutionResult CalculateTestExecutionResultFromTrx(XDocument trx, TestRunConfiguration testRunConfiguration, string output, IEnumerable<string> reportFiles, string logFileContent)
        {
            var testExecutionResult = GetCommonTestExecutionResult(trx, output, reportFiles, logFileContent);

            return CalculateUnitTestProviderSpecificTestExecutionResult(testExecutionResult, testRunConfiguration);
        }

        private TestExecutionResult GetCommonTestExecutionResult(XDocument trx, string output, IEnumerable<string> reportFiles, string logFileContent)
        {
            var xmlns = XNamespace.Get("http://microsoft.com/schemas/VisualStudio/TeamTest/2010");

            var testRunElement = trx.Descendants(xmlns + "TestRun").Single();
            var summaryElement = testRunElement.Element(xmlns + "ResultSummary")?.Element(xmlns + "Counters")
                                 ?? throw new InvalidOperationException("Invalid document; result summary counters element not found.");

            var totalAttribute = summaryElement.Attribute("total");
            var executedAttribute = summaryElement.Attribute("executed");
            var passedAttribute = summaryElement.Attribute("passed");
            var failedAttribute = summaryElement.Attribute("failed");
            var inconclusiveAttribute = summaryElement.Attribute("inconclusive");

            int.TryParse(totalAttribute?.Value, out int total);
            int.TryParse(executedAttribute?.Value, out int executed);
            int.TryParse(passedAttribute?.Value, out int passed);
            int.TryParse(failedAttribute?.Value, out int failed);
            int.TryParse(inconclusiveAttribute?.Value, out int inconclusive);

            var testResults = GetTestResults(testRunElement, xmlns);
            string trxOutput = testResults.Select(r => r.StdOut).Aggregate(new StringBuilder(), (acc, c) => acc.AppendLine(c)).ToString();

            return new TestExecutionResult
            {
                ValidLicense = false,
                TestResults = testResults,
                Output = output,
                ReportFiles = reportFiles.ToList(),
                TrxOutput = trxOutput,
                LogFileContent = logFileContent,
                Total = total,
                Executed = executed,
                Succeeded = passed,
                Failed = failed,
                Pending = inconclusive,
            };
        }

        private TestExecutionResult CalculateUnitTestProviderSpecificTestExecutionResult(TestExecutionResult testExecutionResult, TestRunConfiguration testRunConfiguration)
        {
            switch (testRunConfiguration.UnitTestProvider)
            {
                case UnitTestProvider.xUnit: return CalculateXUnitTestExecutionResult(testExecutionResult);
                case UnitTestProvider.MSTest: return CalculateMsTestTestExecutionResult(testExecutionResult);
                case UnitTestProvider.NUnit3: return CalculateNUnitTestExecutionResult(testExecutionResult);
                case UnitTestProvider.SpecRun: return CalculateSpecRunTestExecutionResult(testExecutionResult);
                default: throw new NotSupportedException($"The specified unit test provider is not supported: {testRunConfiguration.UnitTestProvider}");
            }
        }

        private TestExecutionResult CalculateSpecRunTestExecutionResult(TestExecutionResult testExecutionResult)
        {
            bool FilterIgnored(TestResult testResult) => testResult.StdOut.Contains("-> Ignored");

            bool FilterPending(TestResult testResult) => testResult.StdOut.Contains("TechTalk.SpecRun.PendingTestException")
                                                         || testResult.StdOut.Contains("No matching step definition found for the step.");

            var testResultsWithOutput = testExecutionResult.TestResults.Where(tr => !(tr?.StdOut is null)).ToArray();

            testExecutionResult.Ignored = testResultsWithOutput.Where(FilterIgnored).Count();
            testExecutionResult.Pending = testResultsWithOutput.Where(FilterPending).Count();

            return testExecutionResult;
        }

        private TestExecutionResult CalculateNUnitTestExecutionResult(TestExecutionResult testExecutionResult)
        {
            testExecutionResult.Ignored = GetNUnitIgnoredCount(testExecutionResult);
            testExecutionResult.Pending = testExecutionResult.Total - testExecutionResult.Executed - testExecutionResult.Ignored;

            return testExecutionResult;
        }

        private TestExecutionResult CalculateMsTestTestExecutionResult(TestExecutionResult testExecutionResult)
        {
            testExecutionResult.Ignored = testExecutionResult.TestResults
                                                             .Where(r => r.ErrorMessage != null)
                                                             .Select(r => r.ErrorMessage)
                                                             .Count(m => m.Contains("Assert.Inconclusive failed") && !m.Contains("One or more step definitions are not implemented yet"));


            testExecutionResult.Pending = testExecutionResult.TestResults
                                                             .Where(r => r.ErrorMessage != null)
                                                             .Select(r => r.ErrorMessage)
                                                             .Count(m => m.Contains("Assert.Inconclusive failed. One or more step definitions are not implemented yet."));

            return testExecutionResult;
        }

        private TestExecutionResult CalculateXUnitTestExecutionResult(TestExecutionResult testExecutionResult)
        {
            testExecutionResult.Pending = GetXUnitPendingCount(testExecutionResult.Output);
            testExecutionResult.Failed -= testExecutionResult.Pending;
            testExecutionResult.Ignored = testExecutionResult.Total - testExecutionResult.Executed;

            return testExecutionResult;
        }

        private List<TestResult> GetTestResults(XElement testRunElement, XNamespace xmlns)
        {
            var testResults = from unitTestResultElement in testRunElement.Element(xmlns + "Results")?.Elements(xmlns + "UnitTestResult") ?? Enumerable.Empty<XElement>()
                              let outputElement = unitTestResultElement.Element(xmlns + "Output")
                              let idAttribute = unitTestResultElement.Attribute("executionId")
                              let outcomeAttribute = unitTestResultElement.Attribute("outcome")
                              let stdOutElement = outputElement?.Element(xmlns + "StdOut")
                              let errorInfoElement = outputElement?.Element(xmlns + "ErrorInfo")
                              let errorMessage = errorInfoElement?.Element(xmlns + "Message")
                              where idAttribute != null
                              where outcomeAttribute != null
                              select new TestResult
                              {
                                  Id = idAttribute.Value,
                                  Outcome = outcomeAttribute.Value,
                                  StdOut = stdOutElement?.Value,
                                  ErrorMessage = errorMessage?.Value
                              };

            return testResults.ToList();
        }

        private int GetNUnitIgnoredCount(TestExecutionResult testExecutionResult)
        {
            var elements = from testResult in testExecutionResult.TestResults
                           where testResult.Outcome == "NotExecuted"
                           where testResult.ErrorMessage?.Contains("Scenario ignored using @Ignore tag") == true
                                 || testResult.ErrorMessage?.Contains("Ignored feature") == true
                           select testResult;

            return elements.Count();
        }

        private int GetXUnitPendingCount(string output)
        {
            return Regex.Matches(output, "XUnitPendingStepException").Count / 2 +
                   Regex.Matches(output, "XUnitInconclusiveException").Count / 2;
        }

        private IEnumerable<string> FindFilePath(string[] lines, string ending, string starting)
        {
            return from l in lines
                   let trimmed = l.Trim()
                   let start = trimmed.IndexOf(starting)
                   where trimmed.Contains(starting)
                   where trimmed.EndsWith(ending)
                   select trimmed.Substring(start + starting.Length);
        }

        private string GenereateVsTestsArguments()
        {
            string arguments = $"\"{_testProjectFolders.CompiledAssemblyPath}\" /logger:trx";

            if (_testRunConfiguration.UnitTestProvider != UnitTestProvider.SpecRun)
            {
                if (_testRunConfiguration.ProjectFormat == ProjectFormat.Old)
                {
                    arguments += $" /TestAdapterPath:\"{_testProjectFolders.PathToNuGetPackages}\"";
                }
            }

            if (Filter.IsNotNullOrEmpty())
            {
                arguments += $" /TestCaseFilter:{Filter}";
            }

            if (RunSettingsFile.IsNotNullOrWhiteSpace())
            {
                arguments += $" /Settings:{RunSettingsFile}";
            }

            return arguments;
        }
    }
}
