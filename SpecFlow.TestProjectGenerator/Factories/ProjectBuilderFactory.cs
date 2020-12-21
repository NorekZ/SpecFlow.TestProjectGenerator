﻿using TechTalk.SpecFlow.TestProjectGenerator.ConfigurationModel;
using TechTalk.SpecFlow.TestProjectGenerator.Factories.BindingsGenerator;
using TechTalk.SpecFlow.TestProjectGenerator.Factories.ConfigurationGenerator;
using TechTalk.SpecFlow.TestProjectGenerator.Helpers;
using TechTalk.SpecFlow.TestProjectGenerator.NewApi._1_Memory;

namespace TechTalk.SpecFlow.TestProjectGenerator.Factories
{
    public class ProjectBuilderFactory
    {
        protected readonly FeatureFileGenerator _featureFileGenerator;
        protected readonly Folders _folders;
        protected readonly TargetFrameworkMonikerStringBuilder _targetFrameworkMonikerStringBuilder;
        protected readonly BindingsGeneratorFactory _bindingsGeneratorFactory;
        protected readonly ConfigurationGeneratorFactory _configurationGeneratorFactory;
        protected readonly CurrentVersionDriver _currentVersionDriver;
        protected readonly TestProjectFolders _testProjectFolders;
        protected readonly TestRunConfiguration _testRunConfiguration;

        public ProjectBuilderFactory(
            TestProjectFolders testProjectFolders,
            TestRunConfiguration testRunConfiguration,
            CurrentVersionDriver currentVersionDriver,
            ConfigurationGeneratorFactory configurationGeneratorFactory,
            BindingsGeneratorFactory bindingsGeneratorFactory,
            FeatureFileGenerator featureFileGenerator,
            Folders folders,
            TargetFrameworkMonikerStringBuilder targetFrameworkMonikerStringBuilder)
        {
            _testProjectFolders = testProjectFolders;
            _testRunConfiguration = testRunConfiguration;
            _currentVersionDriver = currentVersionDriver;
            _configurationGeneratorFactory = configurationGeneratorFactory;
            _bindingsGeneratorFactory = bindingsGeneratorFactory;
            _featureFileGenerator = featureFileGenerator;
            _folders = folders;
            _targetFrameworkMonikerStringBuilder = targetFrameworkMonikerStringBuilder;
        }

        public ProjectBuilder CreateProject(string language)
        {
            return CreateProjectInternal(null, ParseProgrammingLanguage(language));
        }

        public ProjectBuilder CreateProject(string projectName, string language)
        {
            return CreateProjectInternal(projectName, ParseProgrammingLanguage(language));
        }

        public ProjectBuilder CreateProject(string projectName, ProgrammingLanguage language)
        {
            return CreateProjectInternal(projectName, language);
        }

        public ProgrammingLanguage ParseProgrammingLanguage(string input)
        {
            switch (input.ToUpper())
            {
                case "CSHARP":
                case "C#": return ProgrammingLanguage.CSharp;
                case "VB":
                case "VB.NET":
                case "VBNET": return ProgrammingLanguage.VB;
                case "FSHARP":
                case "F#": return ProgrammingLanguage.FSharp;
                default: return ProgrammingLanguage.Other;
            }
        }

        private ProjectBuilder CreateProjectInternal(string projectName, ProgrammingLanguage language)
        {
            var project = CreateProjectBuilder();
            project.TargetFramework = _testRunConfiguration.TargetFramework;
            project.Format = _testRunConfiguration.ProjectFormat;
            project.ConfigurationFormat = _testRunConfiguration.ConfigurationFormat;
            project.Language = language;

            project.Configuration.UnitTestProvider = _testRunConfiguration.UnitTestProvider;

            if (projectName.IsNotNullOrWhiteSpace())
            {
                project.ProjectName = projectName;
            }

            return project;
        }

        protected virtual ProjectBuilder CreateProjectBuilder()
        {
            var configuration = new Configuration()
            {
                CucumberMessagesSection = { Enabled = true }
            };

            return new ProjectBuilder(_testProjectFolders, _featureFileGenerator, _bindingsGeneratorFactory, _configurationGeneratorFactory, configuration, _currentVersionDriver, _folders, _targetFrameworkMonikerStringBuilder);
        }
    }
}
