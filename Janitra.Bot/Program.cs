using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Janitra.Bot
{
	class Program
	{
		static void Main(string[] args)
		{
			var services = new ServiceCollection();
			// Set up configuration sources.
			var builder = new ConfigurationBuilder()
				.SetBasePath(Path.Combine(AppContext.BaseDirectory))
				.AddJsonFile("appsettings.json", false);

			var configuration = builder.Build();
			var serviceProvider = services.BuildServiceProvider();

			new JanitraBot(configuration, serviceProvider).Run();
		}
	}
}