using MagicCommunity;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class AcceptanceChecks
{
    internal static void Run(Dictionary<string,bool> checks)
    {
        var now=DateTimeOffset.UtcNow;
        bool Valid(JsonObject permit,bool continuous){try{using var document=JsonDocument.Parse(permit.ToJsonString());NativeAcceptancePolicy.Validate(document.RootElement,continuous,now);return true;}catch(InvalidDataException){return false;}}
        void Check(string name,bool valid){checks[name]=valid;if(!valid)throw new InvalidDataException(name);}
        var timed=new JsonObject{["durationSeconds"]=3600,["launchBeforeUtc"]=now.AddMinutes(5).ToString("o")};
        Check("acceptance-existing-one-hour-permit-still-valid",Valid(timed,false));
        timed["startedUtc"]=now.AddHours(-2).ToString("o");Check("acceptance-timed-permit-cannot-be-reused",!Valid(timed,false));
        var continuous=new JsonObject{["acceptanceMode"]="continuous",["durationSeconds"]=null,["expiresUtc"]=null};
        Check("acceptance-continuous-explicit-no-deadline",Valid(continuous,true));
        Check("acceptance-continuous-permit-requires-matching-selection",!Valid(continuous,false));
        Check("acceptance-timed-permit-cannot-enable-continuous",!Valid(timed,true));
        continuous["startedUtc"]=now.AddYears(-1).ToString("o");Check("acceptance-continuous-allows-manual-relaunch",Valid(continuous,true));
        continuous["expiresUtc"]=now.AddHours(1).ToString("o");Check("acceptance-continuous-rejects-hidden-expiry",!Valid(continuous,true));continuous["expiresUtc"]=null;
        continuous["durationSeconds"]=3600;Check("acceptance-continuous-rejects-hidden-duration",!Valid(continuous,true));
        continuous["acceptanceMode"]="unknown";Check("acceptance-unknown-mode-rejected",!Valid(continuous,true));
        timed.Remove("startedUtc");timed["launchBeforeUtc"]=now.AddSeconds(-1).ToString("o");Check("acceptance-timed-expired-launch-still-rejected",!Valid(timed,false));
    }
}
