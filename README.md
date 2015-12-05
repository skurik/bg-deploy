# bg-deploy

bg-deploy is a way to deploy VS projects to IIS without any downtime experience. The name originates from the related pattern of blue-green deployment, described for example [here](http://martinfowler.com/bliki/BlueGreenDeployment.html). 

While the bg-deploy script does not assume anything about how the target IIS sites are managed, typically it will be used in conjunction with Application Request Routing (ARR) and Server Farms features of IIS and the following text assumes this scenario. For a description of how the IIS may be set up, see e.g. [this great StackOverflow answer](http://serverfault.com/questions/124274/zero-downtime-uploads-rollback-in-iis/126379). By experimenting with the issue myself, I can only add that it does not seem to be necessary for the two actual sites (the "blue" and "green" ones) to be attached to different IPs. I am running this solution on a single machine with a single IP and it works great.

## Prerequisities

### IIS setup

* three sites set up on IIS, as described in the StackOverflow link above
 * the first one serves as the actual gateway to your web application and thus will be typically bound to port 80
 * the other two are the blue and green sites, each bound to a different port and each with its own application pool identity
* server farm spanning the blue and green sites
 * when adding the two servers to the farm, be sure to also specify their port number correctly in the advanced settings
 * health check: create a health test to query `http://{server-name}/up.html` every second with `up` as the response match
* URL rewrite rule to forward requests to your site to the server farm
 * for example, have the following two conditions specified for the rule:
   * `{HTTP_HOST}` matches your server public hostname (e.g. "www.server.com")
    * `{SERVER_PORT}` matches `^80$`
 * as an action, `Route to server farm` and specify the corresponding one

### Deployment machine

This is the machine you will be deploying the project from. Typically the development box or a build server. 

* [scriptcs](https://github.com/scriptcs)
 * after installing scriptcs itself, install the [Newtonsoft.Json](https://www.nuget.org/packages/newtonsoft.json/) NuGet package by running `scriptcs -install Newtonsoft.Json`. Note that in the time of writing this, `scriptcs` does not support v3 NuGet feeds.

### Web application

* add `up.html` with the content `down` as a content file to your web application project. Make sure it is being deployed to IIS along with the rest of the application.
* add an endpoint to retrieve the application startup timestamp. The deployment script relies on that when checking whether the deployed application has already been loaded by IIS. It might be something like this:

```

static class Utils
{
    private static DateTime? _startUpTimeStamp;
    public static DateTime? StartUpTimeStamp
    {
        get
        {
            return _startUpTimeStamp;
        }
        set
        {
            if (_startUpTimeStamp != null)
                throw InvalidOperationException("Can only set this once");
            _startUpTimeStamp = value;
        }
    }
}

public class MyApplication : System.Web.HttpApplication
{
    protected void Application_Start()
    {
        Utils.StartUpTimeStamp = DateTime.UtcNow;
    }
}

public class MyController : ApiController
{
    [Route("api/infrastructure/app-info")]
    public object Get()
    {
        return new { Timestamp = Utils.StartUpTimeStamp.ToString("o") };
    }
}
```

* add an endpoint to update the health status. This is done by rewriting the `up.html` in the site's root folder. Sample implementation:

```
public class HealthStatusApiController : ApiController
{
    [Route("api/infrastructure/health-status"), HttpPost]
    public async Task<HttpResponseMessage> Post()
    {
        var content = await Request.Content.ReadAsStringAsync();
        var statusFilePath = Path.Combine(Path.GetDirectoryName(HttpRuntime.AppDomainAppPath), "up.html");
        
        File.WriteAllText(statusFilePath, content);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}
```

## Configuration

The deployment script is passed a path to a JSON config file. A sample one is part of the repository, the options are explained below (the symmetrical ones are not repeated)


| Option | Meaning |
| ------ | ------- |
| `BlueSiteUrl` | The blue site's URL. Example: http://192.168.1.33:8001 |
| `BlueIisAppPath` | The blue site's name, as configured in IIS. Example: `serverBlue` |
| `GreenSiteUrl` | The green site's URL. Example: http://192.168.1.33:8002 |
| `GreenIisAppPath` | The green site's name, as configured in IIS. Example: `serverGreen` |
| `UpStatusUrlPath` | Health check relative URL endpoint. Example: `/up.html` |
| `UpStatusPattern` | Regex which, if matching, constitutes a healthy site. Example `^up$` |
| `BuildInfoUrlPath` | Relative URL to the build info endpoint (see above). Example: `/api/infrastructure/app-info` |
| `MsBuildPath` | MSBuild executable path. Example: `c:\Program Files (x86)\MSBuild\14.0\bin\MSBuild.exe` |
| `BuildFilePath` | Path to the web application project's `.csproj`. Example: `..\..\MyApp.Site\MyApp.Site.csproj` |
| `BuildConfiguration` | Build configuration to deploy. Example: `Release` |
| `WaitBeforeCheckingUpdatedBuild` | How long the script should wait after deploying the application to check whether it has been reloaded. If you use the `waitChangeNotification` or `maxWaitChangeNotification` options in `web.config`, this should be larger than the maximum of these two. Example (when `maxWaitChangeNotification = 240`): `270` |
| `TargetServer` | URL endpoint for the deployment process. Example: `192.168.1.33:8172` |
| `DeployUserName` | User name to use for the deployment. Example: `WDeployAdmin` |
| `DeployUserPassword` | The deployment user's password |
| `WarmUpUrlPaths` | List of URL endpoints which we hit after the deployment in order to warm the application up before bringing it live. Example: `[ "PUT /api/infrastructure/long-running-warmup", "GET /other-warm-up" ]` |
| `HealthStatusChangeUrlPath` | URL endpoint which allows the script to change the site's health status (see above). Example `/api/infrastructure/health-status` |
| `HealthyPattern` | Content of `UpStatusUrlPath` for a healthy site. Must correspond to `UpStatusPattern`. Example: `up` |
| `UnhealthyPattern` | Content of `UpStatusUrlPath` for an unhealthy site. Example: `down` |

## Running the deployment script

`scriptcs deploy.csx -- config.json`

If the script finishes successfully, the target site will be updated, without any downtime incurred.

## Troubleshooting

Make sure that when instructing a system to run the above command line, the working directory should be set to the one where `deploy.csx` resides.
