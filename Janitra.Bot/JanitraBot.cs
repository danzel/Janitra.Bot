using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ImageSharp;
using ImageSharp.Formats;
using ImageSharp.Processing;
using Janitra.Bot.Api;
using Serilog;

namespace Janitra.Bot
{
	class JanitraBotOptions
	{
		public string AccessKey { get; set; }
		public int JanitraBotId { get; set; }

		public JanitraBotOptions(string accessKey, int janitraBotId)
		{
			AccessKey = accessKey;
			JanitraBotId = janitraBotId;
		}
	}

	class JanitraBot
	{
		private readonly IClient _client;
		private readonly ILogger _logger;

		private readonly int _janitraBotId;
		private readonly string _accessKey;

		private const string BuildsPath = "Builds";
		private const string MoviesPath = "Movies";
		private const string TestRomsPath = "TestRoms";

		//TODO: Platform specific
		private const string CitraExecutable = "citra.exe";

		public JanitraBot(IClient client, ILogger logger, JanitraBotOptions options)
		{
			_janitraBotId = options.JanitraBotId;
			_accessKey = options.AccessKey;

			_client = client;
			_logger = logger;
		}

		public void RunForever()
		{
			Task.Run(() =>
			{
				_logger.Information("Starting to Run Forever");
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
			}).Wait();
		}

		public async Task RunOnce()
		{
			foreach (var dir in new [] { BuildsPath, MoviesPath, TestRomsPath})
			{
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);
			}

			_logger.Information("Checking in");

			//Get what builds and tests should be ran
			var builds = await _client.ApiCitraBuildsListGetAsync();
			var testDefinitions = await _client.ApiTestDefinitionsListGetAsync();

			_logger.Information("Found {buildsCount} active builds, {testsCount} active tests", builds.Count, testDefinitions.Count);

			//TODO: Eventually tidy up old not used any more builds
			foreach (var build in builds)
			{
				//Can't test on this OS
				if (UrlForOurOs(build) == null)
					continue;

				var testResults = await _client.ApiTestResultsListGetAsync(build.CitraBuildId, janitraBotId: _janitraBotId);

				//Check we have a result for every test definition, do those we don't
				foreach (var testDefinition in testDefinitions)
				{
					if (testResults.All(tr => tr.TestDefinitionId != testDefinition.TestDefinitionId))
					{
						await GetBuildIfRequired(build);
						await GetTestDefinitionIfRequired(testDefinition);


						await RunTest(build, testDefinition);
					}
				}
			}
		}

		public async Task RunTest(JsonCitraBuild build, JsonTestDefinition testDefinition)
		{
			_logger.Information("Preparing to Run Test {testDefinitionId} for Build {citraBuildId}", testDefinition.TestDefinitionId, build.CitraBuildId);
			if (File.Exists("screenshot_0.bmp"))
				File.Delete("screenshot_0.bmp");
			if (File.Exists("screenshot_1.bmp"))
				File.Delete("screenshot_1.bmp");

			var executable = GetBuildExecutablePath(build);

			var movieFilename = Path.Combine(MoviesPath, testDefinition.MovieSha256);
			var testRomFilename = Path.Combine(TestRomsPath, testDefinition.TestRom.RomSha256);


			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = $"--movie-play {movieFilename} --movie-test {testRomFilename}",

				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WindowStyle = ProcessWindowStyle.Hidden,

				//TODO? WorkingDirectory = 
			};

			var result = NewTestResultExecutionResult.Completed;

			_logger.Information("Starting test");
			var stopwatch = Stopwatch.StartNew();
			var process = Process.Start(startInfo);

			if (!process.WaitForExit(5 * 60 * 1000))
			{
				process.Kill();
				result = NewTestResultExecutionResult.Timeout;
			}
			stopwatch.Stop();

			if (process.ExitCode != 0)
			{
				result = NewTestResultExecutionResult.Crash;
			}

			_logger.Information("Test finished, result {result}", result);

			var log = await process.StandardOutput.ReadToEndAsync();

			var error = await process.StandardError.ReadToEndAsync();
			if (error.Length > 0)
			{
				log += "\n\nError:\n" + error;
			}

			_logger.Information("Got {logLength} bytes of logs, {errorLength} bytes are from StandardError", log.Length, error.Length);

			var screenshotTop = GetRotatedPngScreenshot("screenshot_0.bmp");
			var screenshotBottom = GetRotatedPngScreenshot("screenshot_1.bmp");

			_logger.Information("Submitting result");
			await _client.ApiTestResultsAddPostAsync(new NewTestResult
			{
				JanitraBotId = _janitraBotId,
				AccessKey = _accessKey,

				CitraBuildId = build.CitraBuildId,
				TestDefinitionId = testDefinition.TestDefinitionId,
				Log = Encoding.UTF8.GetBytes(log),
				ExecutionResult = result,
				TimeTakenSeconds = stopwatch.Elapsed.TotalSeconds,

				ScreenshotTop = screenshotTop,
				ScreenshotBottom = screenshotBottom
			});
			_logger.Information("Done!");
		}

		/// <summary>
		/// Loads the given screenshot, rotates it the right way up and returns it as a PNG
		/// </summary>
		private byte[] GetRotatedPngScreenshot(string path)
		{
			using (var stream = File.OpenRead(path))
			{
				using (var image = ImageSharp.Image.Load(stream))
				{
					using (var output = new MemoryStream())
					{
						image.Rotate(RotateType.Rotate270)
							.Save(output, new PngEncoder { PngColorType = PngColorType.Rgb, CompressionLevel = 9 });
						return output.ToArray();
					}
				}
			}
		}

		/// <summary>
		/// Throws an exception if we fail to fetch the build
		/// </summary>
		private async Task GetBuildIfRequired(JsonCitraBuild build)
		{
			if (!Directory.Exists(BuildsPath))
				Directory.CreateDirectory(BuildsPath);

			var buildPath = Path.Combine(BuildsPath, build.CitraBuildId.ToString());
			if (!Directory.Exists(buildPath) || !Directory.EnumerateFiles(buildPath, CitraExecutable, SearchOption.AllDirectories).Any())
			{
				_logger.Information("Need to download build {buildId}", build.CitraBuildId);
				var client = new HttpClient();
				var result = await client.GetAsync(UrlForOurOs(build));

				if (result.IsSuccessStatusCode)
				{
					_logger.Information("Download Completed, Extracting");
					var tmpFileName = Path.GetTempFileName();
					using (var file = File.OpenWrite(tmpFileName))
						await result.Content.CopyToAsync(file);

					//TODO: Unzip or use others, this is windows zip only
					ZipFile.ExtractToDirectory(tmpFileName, buildPath, true);
				}
				else
				{
					_logger.Error("Download Failed {statusCode} {reason}", result.StatusCode, result.ReasonPhrase);
					throw new Exception("Failed to download build " + build.CitraBuildId);
				}
			}
		}

		private async Task GetTestDefinitionIfRequired(JsonTestDefinition testDefinition)
		{
			//TODO: Test the movie Sha256 matches
			var movieFilename = Path.Combine(MoviesPath, testDefinition.MovieSha256);
			if (!File.Exists(movieFilename))
			{
				_logger.Information("Need to download movie for test {testDefinitionId}", testDefinition.TestDefinitionId);
				var client = new HttpClient();
				var result = await client.GetAsync(testDefinition.MovieUrl);

				if (result.IsSuccessStatusCode)
				{
					_logger.Information("Download Completed, Saving");
					using (var file = File.OpenWrite(movieFilename))
						await result.Content.CopyToAsync(file);
				}
				else
				{
					_logger.Error("Download Failed {statusCode} {reason}", result.StatusCode, result.ReasonPhrase);
					throw new Exception("Failed to download testDefinition.Movie " + testDefinition.TestDefinitionId);
				}
			}

			//TODO: Support other types of test rom
			//TODO: Sha256 check
			var testRomFilename = Path.Combine(TestRomsPath, testDefinition.TestRom.RomSha256);
			if (!File.Exists(testRomFilename))
			{
				_logger.Information("Need to download test ROM for test {testDefinitionId}", testDefinition.TestDefinitionId);
				var client = new HttpClient();
				var result = await client.GetAsync(testDefinition.TestRom.RomUrl);

				if (result.IsSuccessStatusCode)
				{
					_logger.Information("Download Completed, Saving");
					using (var file = File.OpenWrite(testRomFilename))
						await result.Content.CopyToAsync(file);
				}
				else
				{
					_logger.Error("Download Failed {statusCode} {reason}", result.StatusCode, result.ReasonPhrase);
					throw new Exception("Failed to download testDefinition.TestRom " + testDefinition.TestDefinitionId);
				}
			}
		}

		private string GetBuildExecutablePath(JsonCitraBuild build)
		{
			var buildPath = Path.Combine(BuildsPath, build.CitraBuildId.ToString());
			return Directory.EnumerateFiles(buildPath, CitraExecutable, SearchOption.AllDirectories).SingleOrDefault();
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
}