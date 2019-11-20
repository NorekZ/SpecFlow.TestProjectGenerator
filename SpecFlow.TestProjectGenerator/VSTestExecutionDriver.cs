﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using TechTalk.SpecFlow.TestProjectGenerator.Data;
using TechTalk.SpecFlow.TestProjectGenerator.Driver;
using TechTalk.SpecFlow.TestProjectGenerator.Helpers;

namespace TechTalk.SpecFlow.TestProjectGenerator
{
    public class VSTestExecutionDriver
    {
        private readonly TestProjectFolders _testProjectFolders;
        private readonly IOutputWriter _outputWriter;
        private readonly TestRunConfiguration _testRunConfiguration;
        private readonly TRXParser _trxParser;
        private readonly TestSuiteEnvironmentVariableGenerator _testSuiteEnvironmentVariableGenerator;
        private readonly UriCleaner _uriCleaner;

        private const string BeginOfTrxFileLine = "Results File: ";
        private const string BeginOfLogFileLine = "Log file: ";
        private const string BeginOfReportFileLine = @"Report file: ";

        public VSTestExecutionDriver(
            TestProjectFolders testProjectFolders,
            IOutputWriter outputWriter,
            TestRunConfiguration testRunConfiguration,
            TRXParser trxParser,
            TestSuiteEnvironmentVariableGenerator testSuiteEnvironmentVariableGenerator)
        {
            _testProjectFolders = testProjectFolders;
            _outputWriter = outputWriter;
            _testRunConfiguration = testRunConfiguration;
            _uriCleaner = new UriCleaner();
            _trxParser = trxParser;
            _testSuiteEnvironmentVariableGenerator = testSuiteEnvironmentVariableGenerator;
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
            const string dotnetTestPath = "dotnet";

            var envVariables = _testSuiteEnvironmentVariableGenerator.GenerateEnvironmentVariables();

            var processHelper = new ProcessHelper();
            string arguments = $"test {GenerateVsTestsArguments()}";
            ProcessResult processResult;
            try
            {
                processResult = processHelper.RunProcess(_outputWriter, _testProjectFolders.ProjectFolder, dotnetTestPath, arguments, envVariables);
            }
            catch (Exception)
            {
                Console.WriteLine($"running {dotnetTestPath} failed - {_testProjectFolders.CompiledAssemblyPath} {dotnetTestPath} {arguments}");
                throw;
            }

            string output = processResult.CombinedOutput;

            var lines = output.SplitByString(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var trxFiles = FindFilePath(lines, ".trx", BeginOfTrxFileLine).ToArray();
            var logFiles = FindFilePath(lines, ".log", BeginOfLogFileLine).ToArray();

            string logFileContent =
                logFiles.Length == 1
                ? File.ReadAllText(_uriCleaner.ConvertSlashes(_uriCleaner.StripSchema(Uri.UnescapeDataString(logFiles.Single()))))
                : string.Empty;

            var reportFiles = GetReportFiles(output);

            trxFiles.Should().HaveCount(1, $"exactly one TRX file should be generated by VsTest; these have been generated:{Environment.NewLine}{string.Join(Environment.NewLine, trxFiles)}");
            string trxFile = trxFiles.Single();

            LastTestExecutionResult = _trxParser.ParseTRXFile(trxFile, output, reportFiles, logFileContent);
            return LastTestExecutionResult;
        }

        private IEnumerable<string> GetReportFiles(string output)
        {
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(i => i.StartsWith(BeginOfReportFileLine))
                         .Select(i => i.Substring(BeginOfReportFileLine.Length))
                         .Select(i => new Uri(i).AbsolutePath);
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

        private string GenerateVsTestsArguments()
        {
            var argumentsBuilder = new StringBuilder("--logger trx");

            if (_testRunConfiguration.UnitTestProvider != UnitTestProvider.SpecRun) 
            {
                if (_testRunConfiguration.ProjectFormat == ProjectFormat.Old)
                {
                    argumentsBuilder.Append($" -a \"{_testProjectFolders.PathToNuGetPackages}\"");
                }
            }

            if (Filter.IsNotNullOrEmpty())
            {
                argumentsBuilder.Append($" --filter \"{Filter}\"");
            }

            if (RunSettingsFile.IsNotNullOrWhiteSpace())
            {
                argumentsBuilder.Append($" --settings \"{RunSettingsFile}\"");
            }

            argumentsBuilder.Append($" \"{_testProjectFolders.PathToSolutionFile}\"");

            return argumentsBuilder.ToString();
        }
    }
}
