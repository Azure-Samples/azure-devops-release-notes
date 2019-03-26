using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AzureDevOpsReleaseNotes
{
    public static class ReleaseNotesWebHook
    {
        [FunctionName("ReleaseNotesWebHook")]
        public static async void RunAsync([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]HttpRequest req, ILogger log)
        {
            //Extract data from request body
            string requestBody = new StreamReader(req.Body).ReadToEnd();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string releaseName = data?.resource?.release?.name;
            string releaseBody = data?.resource?.release?.description;

            VssBasicCredential credentials = new VssBasicCredential(Environment.GetEnvironmentVariable("DevOps.Username"), Environment.GetEnvironmentVariable("DevOps.AccessToken"));
            VssConnection connection = new VssConnection(new Uri(Environment.GetEnvironmentVariable("DevOps.OrganizationURL")), credentials);

            //Time span of 14 days from today
            var dateSinceLastRelease = DateTime.Today.Subtract(new TimeSpan(14, 0, 0, 0));

            //Accumulate closed work items from the past 14 days in text format
            var workItems = GetClosedItems(connection, dateSinceLastRelease);
            var pulls = GetMergedPRs(connection, dateSinceLastRelease);
            
            //Create a new blob markdown file
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("StorageAccountConnectionString"));
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("releases");
            var blob = container.GetBlockBlobReference(releaseName + ".md");

            //Format text content of blob
            var text = String.Format("# {0} \n {1} \n\n" + "# Work Items Resolved:" + workItems + "\n\n# Changes Merged:" + pulls, releaseName, releaseBody);

            //Add text to blob
            await blob.UploadTextAsync(text);
        }

        public static string GetClosedItems(VssConnection connection, DateTime releaseSpan)
        {
            string project = Environment.GetEnvironmentVariable("DevOps.ProjectName");
            var workItemTrackingHttpClient = connection.GetClient<WorkItemTrackingHttpClient>();
            
            //Query that grabs all of the Work Items marked "Done" in the last 14 days
            Wiql wiql = new Wiql()
            {
                Query = "Select [State], [Title] " +
                        "From WorkItems Where " +
                        "[System.TeamProject] = '" + project + "' " +
                        "And [System.State] = 'Done' " +
                        "And [Closed Date] >= '" + releaseSpan.ToString() + "' " +
                        "Order By [State] Asc, [Changed Date] Desc"
            };

            using (workItemTrackingHttpClient)
            {
                WorkItemQueryResult workItemQueryResult = workItemTrackingHttpClient.QueryByWiqlAsync(wiql).Result;

                if (workItemQueryResult.WorkItems.Count() != 0)
                {
                    List<int> list = new List<int>();
                    foreach (var item in workItemQueryResult.WorkItems)
                    {
                        list.Add(item.Id);
                    }

                    //Extraxt desired work item fields
                    string[] fields = { "System.Id", "System.Title" };
                    var workItems = workItemTrackingHttpClient.GetWorkItemsAsync(list, fields, workItemQueryResult.AsOf).Result;

                    //Format Work Item info into text
                    string txtWorkItems = string.Empty;
                    foreach (var workItem in workItems)
                    {
                        txtWorkItems += String.Format("\n 1. #{0}-{1}", workItem.Id, workItem.Fields["System.Title"]);
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
                       TargetRefName = "refs/heads/master",
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
                        txtPRs += String.Format("\n 1. #{0}-{1}", pull.PullRequestId, pull.Title);
                    }

                    return txtPRs;
                }
                return string.Empty;
            }
        }
    }
}
