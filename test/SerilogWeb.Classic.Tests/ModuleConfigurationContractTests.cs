﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Web;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SerilogWeb.Classic.Tests.Support;
using Xunit;

namespace SerilogWeb.Classic.Tests
{
    /// <summary>
    /// The tests that must pass no matter the API used for the configuration
    /// </summary>
    public abstract class ModuleConfigurationContractTests : IDisposable
    {
        private LoggingLevelSwitch LevelSwitch { get; }
        private List<LogEvent> Events { get; }
        private LogEvent LastEvent => Events.LastOrDefault();
        private FakeHttpApplication App => TestContext.App;
        private TestContext TestContext { get; }

        protected ModuleConfigurationContractTests()
        {
            ResetConfiguration();
            TestContext = new TestContext(new FakeHttpApplication());
            Events = new List<LogEvent>();
            LevelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .WriteTo.Sink(new DelegatingSink(ev => Events.Add(ev)))
                .CreateLogger();
        }

        public void Dispose()
        {
            Log.CloseAndFlush();
            ResetConfiguration();
        }

        protected abstract void ResetConfiguration();
        protected abstract void SetRequestLoggingLevel(LogEventLevel level);
        protected abstract void SetRequestLoggingFilter(Func<HttpContextBase, bool> exclude);
        protected abstract void EnableLogging();
        protected abstract void DisableLogging();
        protected abstract void EnableFormDataLoggingAlways();
        protected abstract void EnableFormDataLoggingAlways(LogEventLevel level);
        protected abstract void EnableFormDataLoggingAlways(LogEventLevel level, bool filterPasswords);
        protected abstract void EnableFormDataLoggingAlways(LogEventLevel level, IEnumerable<string> customBlackList);
        protected abstract void DisableFormDataLogging();
        protected abstract void EnableFormDataLoggingOnlyOnError();
        protected abstract void EnableFormDataLoggingOnMatch(Func<HttpContextBase, bool> matchFunction);
        protected abstract void SetCustomLogger(ILogger logger);
        protected abstract void ResetLogger();

        [Theory]
        [InlineData("GET", "http://www.serilog.net", 403)]
        [InlineData("POST", "https://nblumhardt.com/", 302)]
        [InlineData("HEAD", "http://www.example.org", 200)]
        public void BasicRequestLogging(string httpMethod, string rawUrl, int httpStatus)
        {
            var sleepTimeMilliseconds = 4;

            TestContext.SimulateRequest(httpMethod, rawUrl, httpStatus, sleepTimeMilliseconds);

            var evt = LastEvent;
            Assert.NotNull(evt);
            Assert.Equal(LogEventLevel.Information, evt.Level);
            Assert.Null(evt.Exception);
            Assert.Equal("HTTP {Method} {RawUrl} responded {StatusCode} in {ElapsedMilliseconds}ms", evt.MessageTemplate.Text);

            Assert.Equal($"{typeof(ApplicationLifecycleModule)}", evt.Properties[Constants.SourceContextPropertyName].LiteralValue());
            Assert.Equal(httpMethod, evt.Properties["Method"].LiteralValue());
            Assert.Equal(rawUrl, evt.Properties["RawUrl"].LiteralValue());
            Assert.Equal(httpStatus, evt.Properties["StatusCode"].LiteralValue());

            Assert.False(evt.Properties.ContainsKey("FormData"), "No FormData in default config");

            var recordedElapsed = (long)evt.Properties["ElapsedMilliseconds"].LiteralValue();
            Assert.True(recordedElapsed >= sleepTimeMilliseconds, "recordedElapsed >= sleepTimeMilliseconds");
        }

        [Theory]
        [InlineData(LogEventLevel.Verbose)]
        [InlineData(LogEventLevel.Debug)]
        [InlineData(LogEventLevel.Information)]
        [InlineData(LogEventLevel.Warning)]
        [InlineData(LogEventLevel.Error)]
        [InlineData(LogEventLevel.Fatal)]
        public void RequestLoggingLevel(LogEventLevel requestLoggingLevel)
        {

            SetRequestLoggingLevel(requestLoggingLevel);

            TestContext.SimulateRequest();

            var evt = LastEvent;
            Assert.NotNull(evt);
            Assert.Equal(requestLoggingLevel, evt.Level);
        }

        [Fact]
        public void LogPostedFormData()
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Qux", "Baz" }
            };

            EnableFormDataLoggingAlways();

            TestContext.SimulateForm(formData);

            var formDataProperty = LastEvent.Properties["FormData"];
            Assert.NotNull(formDataProperty);
            var expected = formData.ToSerilogNameValuePropertySequence();
            Assert.Equal(expected.ToString(), formDataProperty.ToString());
        }

        [Fact]
        public void LogPostedFormDataHandlesMultipleValuesPerKey()
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Foo", "Qux" }
            };

            EnableFormDataLoggingAlways();

            TestContext.SimulateForm(formData);

            var formDataProperty = LastEvent.Properties["FormData"] as SequenceValue;
            Assert.NotNull(formDataProperty);
            Assert.Equal(2, formDataProperty.Elements.Count);
            var firstKvp = formDataProperty.Elements.First() as StructureValue;
            Assert.Equal("Foo", firstKvp?.Properties?.FirstOrDefault(p => p.Name == "Name")?.Value?.LiteralValue());
            Assert.Equal("Bar", firstKvp?.Properties?.FirstOrDefault(p => p.Name == "Value")?.Value?.LiteralValue());

            var secondKvp = formDataProperty.Elements.Skip(1).First() as StructureValue;
            Assert.Equal("Foo", secondKvp?.Properties?.FirstOrDefault(p => p.Name == "Name")?.Value?.LiteralValue());
            Assert.Equal("Qux", secondKvp?.Properties?.FirstOrDefault(p => p.Name == "Value")?.Value?.LiteralValue());
        }


        [Fact]
        public void LogPostedFormDataAddsNoPropertyWhenThereIsNoFormData()
        {
            EnableFormDataLoggingAlways();

            TestContext.SimulateForm(new NameValueCollection());

            var evt = LastEvent;
            Assert.NotNull(evt);
            Assert.False(evt.Properties.ContainsKey("FormData"), "evt.Properties.ContainsKey('FormData')");
        }

        [Fact]
        public void LogPostedFormDataTakesIntoAccountFormDataLoggingLevel()
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Qux", "Baz" }
            };

            EnableFormDataLoggingAlways(LogEventLevel.Verbose);

            LevelSwitch.MinimumLevel = LogEventLevel.Information;
            TestContext.SimulateForm(formData);

            // logging postedFormData in Verbose only
            // but current level is Information
            Assert.False(LastEvent.Properties.ContainsKey("FormData"), "evt.Properties.ContainsKey('FormData')");

            LevelSwitch.MinimumLevel = LogEventLevel.Debug;
            TestContext.SimulateForm(formData);

            // logging postedFormData in Verbose only
            // but current level is Debug
            Assert.False(LastEvent.Properties.ContainsKey("FormData"), "evt.Properties.ContainsKey('FormData')");

            LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
            TestContext.SimulateForm(formData);

            var formDataProperty = LastEvent.Properties["FormData"];
            Assert.NotNull(formDataProperty);
            var expected = formData.ToSerilogNameValuePropertySequence();
            Assert.Equal(expected.ToString(), formDataProperty.ToString());
        }

        [Fact]
        public void LogPostedFormDataSetToNeverIgnoresShouldLogPostedFormData()
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Qux", "Baz" }
            };

            DisableFormDataLogging();

            TestContext.SimulateForm(formData);

            Assert.False(LastEvent.Properties.ContainsKey("FormData"), "LastEvent.Properties.ContainsKey('FormData')");
        }

        [Theory]
        [InlineData(200, false)]
        [InlineData(302, false)]
        [InlineData(401, false)]
        [InlineData(403, false)]
        [InlineData(404, false)]
        [InlineData(499, false)]
        [InlineData(500, true)]
        [InlineData(502, true)]
        public void LogPostedFormDataSetToOnlyOnErrorShouldLogPostedFormDataOnErrorStatusCodes(int statusCode, bool shouldLogFormData)
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Qux", "Baz" }
            };

            EnableFormDataLoggingOnlyOnError();

            TestContext.SimulateForm(formData, statusCode);

            Assert.Equal(shouldLogFormData, LastEvent.Properties.ContainsKey("FormData"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LogPostedFormDataSetOnMatchMustUseResultOfShouldLogPostedFormData(bool shouldLogFormData)
        {
            var formData = new NameValueCollection
            {
                {"Foo","Bar" },
                {"Qux", "Baz" }
            };

            EnableFormDataLoggingOnMatch(ctx => shouldLogFormData);

            TestContext.SimulateForm(formData);

            Assert.Equal(shouldLogFormData, LastEvent.Properties.ContainsKey("FormData"));
        }

        [Fact]
        public void FormDataExcludesPasswordKeysByDefault()
        {
            EnableFormDataLoggingAlways(LogEventLevel.Information);

            var formData = new NameValueCollection
            {
                {"password","Foo" },
                {"PASSWORD", "Bar" },
                {"EndWithPassword", "Qux" },
                {"PasswordPrefix", "Baz" },
                {"Other", "Value" }
            };
            var expectedLoggedData = new NameValueCollection
            {
                {"password","********" },
                {"PASSWORD", "********" },
                {"EndWithPassword", "********" },
                {"PasswordPrefix", "********" },
                {"Other", "Value" },
            }.ToSerilogNameValuePropertySequence();

            TestContext.SimulateForm(formData);

            var formDataProperty = LastEvent.Properties["FormData"];
            Assert.NotNull(formDataProperty);
            Assert.Equal(expectedLoggedData.ToString(), formDataProperty.ToString());
        }

        [Fact]
        public void PasswordFilteringCanBeDisabled()
        {
            var formData = new NameValueCollection
            {
                {"password","Foo" },
                {"PASSWORD", "Bar" },
                {"EndWithPassword", "Qux" },
                {"PasswordPrefix", "Baz" },
                {"Other", "Value" }
            };

            EnableFormDataLoggingAlways(LogEventLevel.Information, filterPasswords: false);

            TestContext.SimulateForm(formData);
            var formDataProperty = LastEvent.Properties["FormData"];
            Assert.NotNull(formDataProperty);
            var expectedLoggedData = formData.ToSerilogNameValuePropertySequence();
            Assert.Equal(expectedLoggedData.ToString(), formDataProperty.ToString());
        }

        [Fact]
        public void PasswordBlackListCanBeCustomized()
        {
            EnableFormDataLoggingAlways(LogEventLevel.Information, new List<string>
            {
                "badword", "forbidden", "restricted"
            });

            var formData = new NameValueCollection
            {
                {"password","Foo" },
                {"badword", "Bar" },
                {"VeryBadWord", "Qux" },
                {"forbidden", "Baz" },
                {"ThisIsRestricted", "Value" }
            };
            var expectedLoggedData = new NameValueCollection
            {
                {"password","Foo" },
                {"badword", "********" },
                {"VeryBadWord", "********" },
                {"forbidden", "********" },
                {"ThisIsRestricted", "********" }
            }.ToSerilogNameValuePropertySequence();

            TestContext.SimulateForm(formData);

            var formDataProperty = LastEvent.Properties["FormData"];
            Assert.NotNull(formDataProperty);
            Assert.Equal(expectedLoggedData.ToString(), formDataProperty.ToString());
        }

        [Fact]
        public void EnableDisable()
        {
            DisableLogging();
            TestContext.SimulateRequest();
            Assert.Null(LastEvent);

            EnableLogging();
            TestContext.SimulateRequest();
            Assert.NotNull(LastEvent);
        }

        [Fact]
        public void CustomLogger()
        {
            var myLogEvents = new List<LogEvent>();
            using (var myLogger = new LoggerConfiguration()
                .WriteTo.Sink(new DelegatingSink(ev => myLogEvents.Add(ev)))
                .CreateLogger())
            {
                SetCustomLogger(myLogger);

                TestContext.SimulateRequest();

                Assert.Null(LastEvent);

                var myEvent = myLogEvents.FirstOrDefault();
                Assert.NotNull(myEvent);
                Assert.Equal($"{typeof(ApplicationLifecycleModule)}",
                    myEvent.Properties[Constants.SourceContextPropertyName].LiteralValue());

                myLogEvents.Clear();
                Events.Clear();

                ResetLogger();
                TestContext.SimulateRequest();
                Assert.Null(myLogEvents.FirstOrDefault());
                Assert.NotNull(LastEvent);
            }
        }

        [Fact]
        public void RequestFiltering()
        {
            var ignoredPath = "/ignoreme/";
            var ignoredMethod = "HEAD";
            SetRequestLoggingFilter(ctx =>
                ctx.Request.RawUrl.ToLowerInvariant().Contains(ignoredPath.ToLowerInvariant())
                || ctx.Request.HttpMethod == ignoredMethod);

            TestContext.SimulateRequest("GET", $"{ignoredPath}widgets");
            Assert.Null(LastEvent); // should be filtered out

            TestContext.SimulateRequest(ignoredMethod, "/index.html");
            Assert.Null(LastEvent); // should be filtered out

            TestContext.SimulateRequest("GET", "/index.html");
            Assert.NotNull(LastEvent);
        }

        [Theory]
        [InlineData(500, true)]
        [InlineData(501, true)]
        [InlineData(499, false)]
        public void StatusCodeEqualOrBiggerThan500AreLoggedAsError(int httpStatusCode, bool isLoggedAsError)
        {
            TestContext.SimulateRequest(httpStatusCode: httpStatusCode);

            Assert.NotNull(LastEvent);
            Assert.Equal(isLoggedAsError, LastEvent.Level == LogEventLevel.Error);
        }

        [Fact]
        public void RequestWithServerLastErrorAreLoggedAsErrorWithException()
        {
            var theError = new InvalidOperationException("Epic fail", new NotImplementedException());
            TestContext.SimulateRequest(
                (req) => { },
                () =>
                {
                    App.Context.AddError(theError);
                    Assert.NotNull(App.Server.GetLastError());
                    return new FakeHttpResponse();
                });

            Assert.NotNull(LastEvent);
            Assert.Equal(LogEventLevel.Error, LastEvent.Level);
            Assert.Same(theError, LastEvent.Exception);
        }

        [Fact]
        public void RequestWithoutServerLastErrorButStatusCode500AreLoggedAsErrorWithLastAppErrorInException()
        {
            var firstError = new InvalidOperationException("Epic fail #1", new NotImplementedException());
            var secondError = new InvalidOperationException("Epic fail #2", new NotImplementedException());
            TestContext.SimulateRequest(
                (req) => { },
                () =>
                {
                    App.Context.AddError(firstError);
                    App.Context.AddError(secondError);
                    Assert.NotNull(App.Server.GetLastError());
                    App.Context.ClearError();
                    Assert.Null(App.Server.GetLastError());
                    return new FakeHttpResponse()
                    {
                        StatusCode = 500
                    };
                });

            Assert.NotNull(LastEvent);
            Assert.Equal(LogEventLevel.Error, LastEvent.Level);
            Assert.Same(secondError, LastEvent.Exception);
        }
    }
}
