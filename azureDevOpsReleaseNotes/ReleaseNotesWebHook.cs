using Azure;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AzureDevOpsReleaseNotes
{
    public static class ReleaseNotesWebHook
    {
        [FunctionName("ReleaseNotesWebHook")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            //Extract data from request body
            string requestBody = new StreamReader(req.Body).ReadToEnd();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string releaseName = data?.resource?.release?.name;
            string releaseBody = data?.resource?.release?.description;

            VssBasicCredential credentials = new(Environment.GetEnvironmentVariable("DevOps.Username"), Environment.GetEnvironmentVariable("DevOps.AccessToken"));
            VssConnection connection = new(new Uri(Environment.GetEnvironmentVariable("DevOps.OrganizationURL")), credentials);

            //Time span of 14 days from today
            var dateSinceLastRelease = DateTime.Today.Subtract(new TimeSpan(14, 0, 0, 0));

            //Accumulate closed work items from the past 14 days in text format
            var workItems = GetClosedItems(connection, dateSinceLastRelease);
            var pulls = GetMergedPRs(connection, dateSinceLastRelease);

            //Create a new blob markdown file
            BlobContainerClient container = new(Environment.GetEnvironmentVariable("StorageAccountConnectionString"), "releases");            
            container.CreateIfNotExists();

            //Format text content of blob
            var text = string.Format("# {0} \n {1} \n\n" + "# Work Items Resolved:" + workItems + "\n\n# Changes Merged:" + pulls, releaseName, releaseBody);

            var blob = container.GetAppendBlobClient(releaseName + ".md");
            blob.CreateIfNotExists();

            var stream = new MemoryStream();
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));

            //Append text to blob
            stream.Position = 0;
           await blob.AppendBlockAsync(stream);

            return new OkObjectResult("Release Notes Updated");
    
        }

        public static string GetClosedItems(VssConnection connection, DateTime releaseSpan)
        {
            string project = Environment.GetEnvironmentVariable("DevOps.ProjectName");
            var workItemTrackingHttpClient = connection.GetClient<WorkItemTrackingHttpClient>();

            //Query that grabs all of the Work Items marked "Done" in the last 14 days
            Wiql wiql = new()
            {
                Query = "Select [State], [Title] " +
                        "From WorkItems Where " +
                        "[System.TeamProject] = '" + project + "' " +
                        "And [System.State] = 'Resolved' " +
                        "OR [System.State] = 'Closed' " +
                        "And [Closed Date] >= '" + releaseSpan.ToString() + "' " +
                        "Order By [State] Asc, [Changed Date] Desc"
            };

            using (workItemTrackingHttpClient)
            {
                WorkItemQueryResult workItemQueryResult = workItemTrackingHttpClient.QueryByWiqlAsync(wiql).Result;

                if (workItemQueryResult.WorkItems.Count() != 0)
                {
                    List<int> list = new();
                    foreach (var item in workItemQueryResult.WorkItems)
                    {
                        list.Add(item.Id);
                    }

                    //Extract desired work item fields
                    string[] fields = { "System.Id", "System.Title" };
                    var workItems = workItemTrackingHttpClient.GetWorkItemsAsync(list, fields, workItemQueryResult.AsOf).Result;

                    //Format Work Item info into text
                    string txtWorkItems = string.Empty;
                    foreach (var workItem in workItems)
                    {
                        txtWorkItems += string.Format("\n 1. #{0}-{1}", workItem.Id, workItem.Fields["System.Title"]);
                    }
                    return txtWorkItems;
                }
                return string.Empty;
            }
        }

        public static string GetMergedPRs(VssConnection connection, DateTime releaseSpan)
        {
            string projectName = Environment.GetEnvironmentVariable("DevOps.ProjectName");
            var gitClient = connection.GetClient<GitHttpClient>();

            using (gitClient)
            {
                //Get first repo in project
                var releaseRepo = gitClient.GetRepositoriesAsync().Result[0];

                //Grabs all completed PRs merged into master branch
                List<GitPullRequest> prs = gitClient.GetPullRequestsAsync(
                   releaseRepo.Id,
                   new GitPullRequestSearchCriteria()
                   {
                       TargetRefName = "refs/heads/main",
                       Status = PullRequestStatus.Completed

                   }).Result;

                if (prs.Count != 0)
                {
                    //Query that grabs PRs merged since the specified date
                    var pulls = from p in prs
                                where p.ClosedDate >= releaseSpan
                                select p;

                    //Format PR info into text
                    var txtPRs = string.Empty;
                    foreach (var pull in pulls)
                    {
                        txtPRs += string.Format("\n 1. #{0}-{1}", pull.PullRequestId, pull.Title);
                    }

                    return txtPRs;
                }
                return string.Empty;
            }
        }
    }
}
