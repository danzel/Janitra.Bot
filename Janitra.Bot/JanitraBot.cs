using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Janitra.Bot.Api;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Janitra.Bot
{
	class JanitraBotOptions
	{
		public string AccessKey { get; }
		public int JanitraBotId { get; }
		public string ProfileDir { get; }

		public JanitraBotOptions(string accessKey, int janitraBotId, string profileDir)
		{
			AccessKey = accessKey;
			JanitraBotId = janitraBotId;
			ProfileDir = profileDir;
		}
	}

	class CitraSettings
	{
		public int RegionValue { get; set; }
	}

	class JanitraBot
	{
		private readonly IClient _client;
		private readonly ILogger _logger;
		public readonly JanitraBotOptions Options;
		private readonly List<Rom> _roms;

		private const string BuildsPath = "Builds";
		private const string MoviesPath = "Movies";
		private const string TestRomsPath = "TestRoms";
		private const string TempPath = "TempOutput";

		//TODO: Platform specific
		private const string CitraExecutable = "citra.exe";

		public JanitraBot(IClient client, ILogger logger, JanitraBotOptions options, List<Rom> roms)
		{
			_client = client;
			_logger = logger;
			Options = options;
			_roms = roms;
		}

		private void PlaceProfile()
		{
			_logger.Information("Placing profile from {location}", Options.ProfileDir);

			//TODO: Windows only...
			var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); //This returns the path with roaming in
			var destination = Path.Combine(appDataPath, "Citra");

			if (Directory.Exists(destination))
			{
				_logger.Information("Removing existing Citra dir");
				Directory.Delete(destination, true);
			}

			_logger.Information("Copying profile {location}", Options.ProfileDir);
			var source = Path.Combine("Profiles", Options.ProfileDir);

			//Create all of the directories
			foreach (string dirPath in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(dirPath.Replace(source, destination));

			//Copy all the files & Replaces any files with the same name
			foreach (string newPath in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
				File.Copy(newPath, newPath.Replace(source, destination), true);
		}

		private void SetProfileRegion(int region)
		{
			_logger.Information("Setting region to {region}", region);

			//TODO: Windows only...
			var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); //This returns the path with roaming in
			var destination = Path.Combine(appDataPath, "Citra");

			var filename = Path.Combine(destination, "config", "sdl2-config.ini");

			bool found = false;
			var lines = File.ReadAllLines(filename);
			for (var i = 0; i < lines.Length; i++)
			{
				if (lines[i].StartsWith("region_value ="))
				{
					lines[i] = "region_value = " + region;
					found = true;
				}
			}

			if (!found)
				throw new Exception("Couldn't find region_value in citra config to replace");
			File.WriteAllLines(filename, lines);
		}

		public void RunForever()
		{
			Task.Run(async () =>
			{
				_logger.Information("Starting to Run Forever");
				while (true)
				{
					try
					{
						await RunOnce();
					}
					catch (Exception e)
					{
						_logger.Fatal(e, "Error in RunOnce");
						throw;
					}
					await Task.Delay(TimeSpan.FromMinutes(1));
				}
			}).Wait();
		}

		public async Task RunOnce()
		{
			foreach (var dir in new[] { BuildsPath, MoviesPath, TestRomsPath, TempPath })
			{
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);
			}

			_logger.Information("Checking in");

			//Get what builds and tests should be ran
			var builds = await _client.ApiCitraBuildsListGetAsync();

			//await FetchAndRunTests(builds);
			await RunRomMovies(builds);
		}

		private async Task RunRomMovies(List<JsonCitraBuild> builds)
		{
			var roms = await _client.ApiRomsListGetAsync();
			var currentResults = await _client.ApiRomMovieResultsListGetAsync(Options.JanitraBotId);

			_logger.Information("Found {buildsCount} active builds, {romsCount} testable roms", builds.Count, roms.Count);

			foreach (var rom in roms)
			{
				var matching = _roms.Where(r => r.FileName.ToLowerInvariant() == rom.RomFileName.ToLowerInvariant()).ToArray();
				if (matching.Length > 0)
				{
					var romToUse = matching.FirstOrDefault(r => r.Sha256Hash == rom.RomSha256) ?? matching.First();

					if (romToUse.Sha256Hash != rom.RomSha256)
						_logger.Warning("Will run rom {rom} but hashes dont match", rom.Name);

					var movies = await _client.ApiRomMoviesListByRomIdGetAsync(rom.RomId);
					foreach (var movie in movies)
					{
						foreach (var build in builds)
						{
							if (!currentResults.Any(r => r.CitraBuildId == build.CitraBuildId && r.RomMovieId == movie.RomMovieId))
							{
								await GetBuildIfRequired(build);
								await GetMovieIfRequired(movie.MovieUrl, movie.MovieSha256);

								await RunRomMovie(build, rom, romToUse, movie);
							}
						}
					}
				}
				else
				{
					_logger.Debug("Not running {rom}, we don't have it", rom.Name);
				}
			}
		}

		private async Task FetchAndRunTests(List<JsonCitraBuild> builds)
		{
			var testDefinitions = await _client.ApiTestDefinitionsListGetAsync();

			_logger.Information("Found {buildsCount} active builds, {testsCount} active tests", builds.Count, testDefinitions.Count);

			_logger.Information("Loading our existing results");
			var testResults = await _client.ApiTestResultsListGetAsync(Options.JanitraBotId);
			_logger.Information("We have {resultCount} results already", testResults.Count);

			//TODO: Eventually tidy up old not used any more builds
			foreach (var build in builds)
			{
				//Can't test on this OS
				if (UrlForOurOs(build) == null)
					continue;

				//Check we have a result for every test definition, do those we don't
				foreach (var testDefinition in testDefinitions)
				{
					if (!testResults.Any(tr => tr.CitraBuildId == build.CitraBuildId && tr.TestDefinitionId == testDefinition.TestDefinitionId))
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

			var movieFilename = Path.GetFullPath(Path.Combine(MoviesPath, testDefinition.MovieSha256));
			var testRomFilename = Path.GetFullPath(Path.Combine(TestRomsPath, testDefinition.TestRom.RomSha256));

			var runResult = await RunCitra(build, movieFilename, testRomFilename, "--movie-test", null);

			//TODO: If the app crashed or timed out there won't be any screenshot files
			var screenshotTop = GetRotatedPngScreenshot(Path.Combine(TempPath, "screenshot_top.bmp"));
			var screenshotBottom = GetRotatedPngScreenshot(Path.Combine(TempPath, "screenshot_bottom.bmp"));

			_logger.Information("Submitting result");
			await _client.ApiTestResultsAddPostAsync(new NewTestResult
			{
				JanitraBotId = Options.JanitraBotId,
				AccessKey = Options.AccessKey,

				CitraBuildId = build.CitraBuildId,
				TestDefinitionId = testDefinition.TestDefinitionId,
				Log = Encoding.UTF8.GetBytes(runResult.Log),
				ExecutionResult = runResult.ExecutionResult,
				TimeTakenSeconds = runResult.Elapsed.TotalSeconds,

				ScreenshotTop = screenshotTop,
				ScreenshotBottom = screenshotBottom
			});
			_logger.Information("Done!");
		}

		private async Task<RunResult> RunCitra(JsonCitraBuild build, string movieFilename, string testRomFilename, string mode, CitraSettings citraSettings)
		{
			PlaceProfile();

			if (citraSettings != null)
			{
				SetProfileRegion(citraSettings.RegionValue);
			}

			foreach (var file in Directory.EnumerateFiles(TempPath))
				File.Delete(file);

			var executable = GetBuildExecutablePath(build);

			var process = new Process();
			process.StartInfo.FileName = executable;
			process.StartInfo.Arguments = $"--movie-play \"{movieFilename}\" {mode} \"{testRomFilename}\"";
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.WorkingDirectory = Path.GetFullPath(TempPath);

			var log = new StringBuilder();

			process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs eventArgs)
			{
				lock (log)
					log.AppendLine(eventArgs.Data);
			};
			process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs eventArgs) {
				lock (log)
					log.AppendLine(eventArgs.Data);
			};

			var result = NewTestResultExecutionResult.Completed;

			_logger.Information("Starting test");
			var stopwatch = Stopwatch.StartNew();
			process.Start();
			process.BeginErrorReadLine();
			process.BeginOutputReadLine();

			if (!process.WaitForExit(5 * 60 * 1000))
			//if (!process.WaitForExit(10 * 1000))
			{
				process.Kill();
				result = NewTestResultExecutionResult.Timeout;
			}
			stopwatch.Stop();

			if (process.ExitCode != 0 && result != NewTestResultExecutionResult.Timeout)
			{
				result = NewTestResultExecutionResult.Crash;
			}

			_logger.Information("Test finished, result {result}", result);

			_logger.Information("Got {logLength} bytes of logs", log.Length);


			var runResult = new RunResult
			{
				Elapsed = stopwatch.Elapsed,
				ExecutionResult = result,
				Log = log.ToString()
			};
			return runResult;
		}

		private async Task RunRomMovie(JsonCitraBuild build, JsonRom rom, Rom romToUse, JsonRomMovie movie)
		{
			_logger.Information("Preparing to Run Rom {romName} with Movie {movie} for Build {citraBuildId}", rom.Name, movie.Name, build.CitraBuildId);

			var movieFilename = Path.GetFullPath(Path.Combine(MoviesPath, movie.MovieSha256));

			var settings = new CitraSettings
			{
				RegionValue = movie.CitraRegionValue
			};

			var result = await RunCitra(build, movieFilename, romToUse.FullPath, "--movie-test-continuous", settings);

			_logger.Information("Finished running {romName}, result: {executionResult}", rom.Name, result.ExecutionResult);

			var screenshots = new List<NewScreenshot>();

			foreach (var file in Directory.EnumerateFiles(TempPath, "*_top.bmp"))
			{
				//screenshot_%i_top.bmp (and bottom)
				screenshots.Add(new NewScreenshot
				{
					FrameNumber = int.Parse(Path.GetFileName(file).Replace("screenshot_", "").Replace("_top.bmp", "")),
					TopImage = GetRotatedPngScreenshot(file),
					BottomImage = GetRotatedPngScreenshot(file.Replace("_top.bmp", "_bottom.bmp"))
				});
			}
				
			await _client.ApiRomMovieResultsAddPostAsync(new NewRomMovieResult
			{
				CitraBuildId = build.CitraBuildId,
				JanitraBotId = Options.JanitraBotId,
				RomMovieId = movie.RomMovieId,
				AccessKey = Options.AccessKey,
				Log = Encoding.UTF8.GetBytes(result.Log),
				ExecutionResult = Enum.Parse<NewRomMovieResultExecutionResult>(result.ExecutionResult.ToString()),
				Screenshots = screenshots,
				TimeTakenSeconds = result.Elapsed.TotalSeconds
			});
		}

		/// <summary>
		/// Loads the given screenshot, rotates it the right way up and returns it as a PNG
		/// </summary>
		private byte[] GetRotatedPngScreenshot(string path)
		{
			using (var stream = File.OpenRead(path))
			{
				using (var image = Image.Load(stream))
				{
					using (var output = new MemoryStream())
					{
						image.Mutate(i => i.Rotate(RotateType.Rotate270));
						image.Save(output, new PngEncoder { PngColorType = PngColorType.Rgb, CompressionLevel = 9 });
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
			await GetMovieIfRequired(testDefinition.MovieUrl, testDefinition.MovieSha256);

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

		private async Task GetMovieIfRequired(string url, string sha256)
		{
			//TODO: Test the movie Sha256 matches
			var movieFilename = Path.Combine(MoviesPath, sha256);
			if (!File.Exists(movieFilename))
			{
				_logger.Information("Need to download movie {movieHash}", sha256);
				var client = new HttpClient();
				var result = await client.GetAsync(url);

				if (result.IsSuccessStatusCode)
				{
					_logger.Information("Download Completed, Saving");
					using (var file = File.OpenWrite(movieFilename))
						await result.Content.CopyToAsync(file);
				}
				else
				{
					_logger.Error("Download Failed {statusCode} {reason}", result.StatusCode, result.ReasonPhrase);
					throw new Exception("Failed to download Movie " + url);
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

		class RunResult
		{
			public NewTestResultExecutionResult ExecutionResult { get; set; }
			public TimeSpan Elapsed { get; set; }
			public string Log { get; set; }
		}
	}
}