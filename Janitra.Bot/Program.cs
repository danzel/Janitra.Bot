using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Janitra.Bot
{
	class Program
	{
		static void Main(string[] args)
		{
			// Set up configuration sources.
			var configuration = new ConfigurationBuilder()
				.SetBasePath(Path.Combine(AppContext.BaseDirectory))
				.AddJsonFile("appsettings.json", false)
				.Build();

			var services = new ServiceCollection();

			var logger = new LoggerConfiguration()
				.ReadFrom.Configuration(configuration)
				.CreateLogger();
			services.AddSingleton<ILogger>(logger);


			var serviceProvider = services.BuildServiceProvider();

			new JanitraBot(configuration, serviceProvider).RunForever();
		}
	}
}