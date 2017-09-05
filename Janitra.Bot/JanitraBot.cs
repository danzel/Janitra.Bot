using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Janitra.Bot.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Janitra.Bot
{
	class JanitraBot
	{
		private readonly IClient _client;
		private readonly int _janitraBotId;
		private readonly string _authToken;

		public JanitraBot(IConfigurationRoot configuration, ServiceProvider serviceProvider)
		{
			_client = serviceProvider.GetService<IClient>();

			_janitraBotId = int.Parse(configuration["JanitraBotId"]);
			_authToken = configuration["AuthToken"];
		}

		public void Run()
		{
			Task.Run(() =>
			{
				while (true)
				{
					try
					{
						RunOnce();
					}
					catch (Exception e)
					{
						Console.WriteLine(e);
						throw;
					}
					Task.Delay(TimeSpan.FromMinutes(1));
				}
			});
		}

		public async void RunOnce()
		{
			//Get what builds and tests should be ran
			var builds = await _client.CitraBuildsListGetAsync();
			var testDefinitions = await _client.TestDefinitionsListGetAsync();

			foreach (var build in builds)
			{
				var testResults = await _client.TestResultsListGetAsync(build.CitraBuildId, janitraBotId: _janitraBotId);

				//Check we have a result for every test definition, do those we don't
				foreach (var testDefinition in testDefinitions)
				{
					if (testResults.All(tr => tr.TestDefinitionId != testDefinition.TestDefinitionId))
					{
						await RunTest(build, testDefinition);
					}
				}
			}
		}

		public async Task RunTest(JsonCitraBuild build, JsonTestDefinition testDefinition)
		{
			await GetBuildIfRequired(build.CitraBuildId);
			var executable = GetBuildExecutablePath(build.CitraBuildId);

			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = "--help TODO", //TODO

				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WindowStyle = ProcessWindowStyle.Hidden,

				//TODO? WorkingDirectory = 
			};

			var result = NewTestResultTestResultType.Completed;

			var process = Process.Start(startInfo);

			if (!process.WaitForExit(5 * 60 * 1000))
			{
				process.Kill();
				result = NewTestResultTestResultType.Timeout;
			}

			if (process.ExitCode != 0)
			{
				result = NewTestResultTestResultType.Crash;
			}

			var log = await process.StandardOutput.ReadToEndAsync();

			var error = await process.StandardError.ReadToEndAsync();
			if (error.Length > 0)
			{
				log += "\n\nError:\n" + error;
			}

			await _client.TestResultsAddPostAsync(new NewTestResult
			{
				CitraBuildId = build.CitraBuildId,
				TestDefinitionId = testDefinition.TestDefinitionId,
				JanitraBotId = _janitraBotId,
				Log = Encoding.UTF8.GetBytes(log),
				TestResultType = result,
				//TODO: Screenshots
			});
		}

		private string UrlForOurOs(JsonCitraBuild build)
		{
			switch (Environment.OSVersion.Platform)
			{
				case PlatformID.MacOSX:
					return build.OsxUrl;
				case PlatformID.Unix:
					return build.LinuxUrl;
				case PlatformID.Win32NT:
					return build.WindowsUrl;
				default:
					throw new Exception("Don't know what platform matches " + Environment.OSVersion.Platform);
			}
		}
}