using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class MockCodexAppServer
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    private static int Main(string[] args)
    {
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            IDictionary<string, object> request = Json.DeserializeObject(line) as IDictionary<string, object>;
            if (request == null)
                continue;
            if (String.Equals(Environment.GetEnvironmentVariable("MOCK_CODEX_HANG"), "1", StringComparison.Ordinal))
                continue;
            string method = Convert.ToString(request["method"]);
            if (method == "initialized")
                continue;

            object id = request.ContainsKey("id") ? request["id"] : null;
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (method == "account/read")
            {
                result["account"] = ObjectOf(
                    "type", "chatgpt",
                    "email", "test@example.com",
                    "planType", "pro");
                result["requiresOpenaiAuth"] = true;
            }
            else if (method == "account/rateLimits/read")
            {
                Dictionary<string, object> limits = new Dictionary<string, object>();
                if (String.Equals(Environment.GetEnvironmentVariable("MOCK_CODEX_SINGLE_WEEKLY"), "1", StringComparison.Ordinal))
                {
                    limits["primary"] = ObjectOf(
                        "usedPercent", 9,
                        "windowDurationMins", 10080,
                        "resetsAt", 1785312000);
                    limits["secondary"] = null;
                }
                else
                {
                    limits["primary"] = ObjectOf(
                        "usedPercent", 7,
                        "windowDurationMins", 300,
                        "resetsAt", 1784707200);
                    limits["secondary"] = ObjectOf(
                        "usedPercent", 1,
                        "windowDurationMins", 10080,
                        "resetsAt", 1785312000);
                }
                limits["rateLimitReachedType"] = null;
                result["rateLimits"] = limits;
                result["rateLimitResetCredits"] = ObjectOf(
                    "availableCount", 2,
                    "credits", new object[0]);
            }
            else if (method == "account/usage/read")
            {
                result["summary"] = ObjectOf(
                    "lifetimeTokens", 350000000L,
                    "peakDailyTokens", 160000000L);
            }

            Dictionary<string, object> response = new Dictionary<string, object>();
            response["id"] = id;
            response["result"] = result;
            Console.WriteLine(Json.Serialize(response));
            Console.Out.Flush();
        }
        return 0;
    }

    private static Dictionary<string, object> ObjectOf(params object[] pairs)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        for (int index = 0; index + 1 < pairs.Length; index += 2)
            result[Convert.ToString(pairs[index])] = pairs[index + 1];
        return result;
    }
}
