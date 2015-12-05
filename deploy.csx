using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

var configPath = Env.ScriptArgs[0];
Console.WriteLine(String.Format("Reading config from {0}", configPath));

var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
Deployer.Run(cfg);

public class Config
{
	public string BlueSiteUrl { get; set; }
	public string BlueIisAppPath { get; set; }

	public string GreenSiteUrl { get; set; }
	public string GreenIisAppPath { get; set; }

	public string UpStatusUrlPath { get; set; }
	public string UpStatusPattern { get; set; }
	public string MsBuildPath { get; set; }

	public int WaitBeforeCheckingUpdatedBuild { get; set; }
	public string BuildInfoUrlPath { get; set; }
	public string BuildFilePath { get; set; }
	public string BuildConfiguration { get; set; }

	public string TargetServer { get; set; }
	public string DeployUserName { get; set; }
	public string DeployUserPassword { get; set; }
	public List<string> WarmUpUrlPaths { get; set; }
	public string HealthStatusChangeUrlPath { get; set; }
	public string HealthyPattern { get; set; }
	public string UnhealthyPattern { get; set; }
	public string PublicTestUrl { get; set; }
}

public static class ConfigVerifier
{	
	public static void Verify(Config cfg)
	{
		if (!File.Exists(cfg.MsBuildPath))
			throw new Exception(String.Format("MSBuild cannot be found at {0}", cfg.MsBuildPath));
	}
}

public static class Deployer
{
	class NodeDistribution
	{
		public NodeDistribution(string iisAppPath, string healthySiteBaseUrl, string unhealthySiteBaseUrl)
		{
			IisAppPath = iisAppPath;			
			HealthySiteBaseUrl = healthySiteBaseUrl;
			UnhealthySiteBaseUrl = unhealthySiteBaseUrl;
		}

		public string IisAppPath { get; private set; }		
		public string HealthySiteBaseUrl { get; private set; }
		public string UnhealthySiteBaseUrl { get; private set; }
	}

	class BuildInfo
	{
		public string Timestamp { get; set; }
	}

	private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
	public static void Run(Config cfg)
	{
		ConfigVerifier.Verify(cfg);

		var urls = GetUrls(cfg);
		var buildInfo = GetBuildInfo(urls.UnhealthySiteBaseUrl + cfg.BuildInfoUrlPath);
		var msBuildCmdLine = GetMsBuildCommandLine(cfg, urls);
		Console.WriteLine(msBuildCmdLine);
		RunMsBuild(cfg.MsBuildPath, msBuildCmdLine);
		VerifyAfterDeployStatus(urls.HealthySiteBaseUrl + cfg.UpStatusUrlPath, urls.UnhealthySiteBaseUrl + cfg.UpStatusUrlPath, cfg.UpStatusPattern);
		WaitForAppUpdate(urls.UnhealthySiteBaseUrl + cfg.BuildInfoUrlPath, cfg, buildInfo);
		WarmUp(urls.UnhealthySiteBaseUrl, cfg.WarmUpUrlPaths);

		ChangeHealthStatus(urls.UnhealthySiteBaseUrl + cfg.HealthStatusChangeUrlPath, cfg.HealthyPattern);
		if (!IsHealthy(urls.UnhealthySiteBaseUrl + cfg.UpStatusUrlPath, cfg.UpStatusPattern))
			throw new Exception(String.Format("Expected the deployed site at {0} to be healthy now", urls.UnhealthySiteBaseUrl + cfg.UpStatusUrlPath));

		ChangeHealthStatus(urls.HealthySiteBaseUrl + cfg.HealthStatusChangeUrlPath, cfg.UnhealthyPattern);
	}

	private static void VerifyAfterDeployStatus(string healthySiteStatusUrl, string unhealthySiteStatusUrl, string pattern)
	{
		if (!IsHealthy(healthySiteStatusUrl, pattern))
			throw new Exception(String.Format("The site at {0} was supposed to stay healthy after deploy!", healthySiteStatusUrl));

		if (IsHealthy(unhealthySiteStatusUrl, pattern))
			throw new Exception(String.Format("The site at {0} was supposed to stay unhealthy after deploy!", unhealthySiteStatusUrl));
	}

	private static void RunMsBuild(string executablePath, string commandLine)
	{
		ProcessStartInfo psi = new ProcessStartInfo(executablePath, commandLine);
		psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal;
		psi.UseShellExecute = false;
		var process = Process.Start(psi);		
		process.WaitForExit();
	}

	private static string GetMsBuildCommandLine(Config cfg, NodeDistribution nodeDistribution)
	{
		var template = "{5} /P:Configuration={0} /P:DeployIisAppPath={1} /P:Platform=AnyCPU /P:DeployOnBuild=True /P:DeployTarget=MSDeployPublish /P:MsDeployServiceUrl=https://{2}/MsDeploy.axd /P:AllowUntrustedCertificate=True /P:MSDeployPublishMethod=WMSvc /P:CreatePackageOnPublish=True /P:UserName={3} /P:Password={4} /verbosity:normal";
		var cmdLine = String.Format(template, cfg.BuildConfiguration, nodeDistribution.IisAppPath, cfg.TargetServer, cfg.DeployUserName, cfg.DeployUserPassword, cfg.BuildFilePath);
		return cmdLine;
	}

	private static void ChangeHealthStatus(string url, string healthStatus)
	{
		Console.WriteLine("Setting health status to '{0}' at {1}", healthStatus, url);
		var response = client.PostAsync(url, new StringContent(healthStatus)).Result;
		if (!response.IsSuccessStatusCode)
			throw new Exception(String.Format("Error response: {0}", response.StatusCode));
	}

	private static void WarmUp(string baseUrl, IEnumerable<string> urlPaths)
	{
		foreach (var urlPath in urlPaths)
		{
			Console.WriteLine("Warming up by issuing {0}", urlPath);
			var sw = Stopwatch.StartNew();
			var request = BuildRequest(baseUrl, urlPath);
			var response = client.SendAsync(request).Result;
			if (!response.IsSuccessStatusCode)
				throw new Exception(String.Format("Warm-up request to {0} returned an error response: {1}", urlPath, response.StatusCode));
			sw.Stop();
			Console.WriteLine("\tTook {0}", sw.Elapsed);
		}
	}

	private static HttpRequestMessage BuildRequest(string baseUrl, string descriptor)
	{
		var verb = descriptor.Split(' ').First();
		var url = descriptor.Split(' ').Last();

		return new HttpRequestMessage(new HttpMethod(verb), baseUrl + url);
	}

	private static NodeDistribution GetUrls(Config cfg)
	{
		var blueIsHealthy = IsHealthy(cfg.BlueSiteUrl + cfg.UpStatusUrlPath, cfg.UpStatusPattern);
		var greenIsHealthy = IsHealthy(cfg.GreenSiteUrl + cfg.UpStatusUrlPath, cfg.UpStatusPattern);

		Console.WriteLine(String.Format("Blue is {0}healthy", blueIsHealthy ? "" : "NOT "));
		Console.WriteLine(String.Format("Green is {0}healthy", greenIsHealthy ? "" : "NOT "));

		if (blueIsHealthy != !greenIsHealthy)
			throw new Exception(String.Format("Cannot have both nodes {0} at the same time!", blueIsHealthy ? "live" : "dead"));

		var healthyBaseUrl = blueIsHealthy ? cfg.BlueSiteUrl : cfg.GreenSiteUrl;
		var unhealthyBaseUrl = blueIsHealthy ? cfg.GreenSiteUrl : cfg.BlueSiteUrl;

		return new NodeDistribution(
			blueIsHealthy ? cfg.GreenIisAppPath : cfg.BlueIisAppPath,			
			healthyBaseUrl,			
			unhealthyBaseUrl);
	} 

	private static bool IsHealthy(string url, string pattern)
	{
		var response = client.GetAsync(url).Result;
		var content = response.Content.ReadAsStringAsync().Result;

		Console.WriteLine(String.Format("Response from {0}: {1}", url, content));

		return Regex.IsMatch(content, pattern);
	}

	private static BuildInfo GetBuildInfo(string url)
	{
		var response = client.GetAsync(url).Result;
		var content = response.Content.ReadAsStringAsync().Result;

		return JsonConvert.DeserializeObject<BuildInfo>(content);
	}

	private static void WaitForAppUpdate(string url, Config cfg, BuildInfo currentBuildInfo)
	{		
		var sw = Stopwatch.StartNew();
		GetBuildInfo(url);
		Console.WriteLine("Waiting {0} seconds before checking whether the application has been updated", cfg.WaitBeforeCheckingUpdatedBuild);
		
		Thread.Sleep(cfg.WaitBeforeCheckingUpdatedBuild * 1000);
		Console.WriteLine("Checking whether the application has been updated");
		var buildInfo = GetBuildInfo(url);
		while (buildInfo.Timestamp == currentBuildInfo.Timestamp)
		{
			Console.WriteLine(String.Format("After {0}, build is still not updated ({1})", sw.Elapsed, buildInfo.Timestamp));
			Thread.Sleep(5000);
			buildInfo = GetBuildInfo(url);
		}

		sw.Stop();
		Console.WriteLine(String.Format("Updated build detected after {0}: {1} now vs. {2} before", sw.Elapsed, buildInfo.Timestamp, currentBuildInfo.Timestamp));
	}
}
