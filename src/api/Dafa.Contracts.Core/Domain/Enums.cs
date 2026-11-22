namespace Dafa.Contracts.Core.Domain;

public enum ContractStatus { Draft, InApproval, Approved, Active, NoticeGiven, Expired, Terminated }

public enum ReminderRecipient { Owner, Delegate, Responsible }

public enum RenewalDecision { Renew, Renegotiate, GiveNotice, LetExpire }

public enum LinkKind { Amendment, Appendix, Framework, Related }

public enum ObligationStatus { Open, Done }

public enum DocumentKind { Main, Appendix, Amendment, ApprovalLog, Other }

public enum RetentionAction { ReviewThenDelete, DeleteAutomatically, Keep }

public enum ApprovalStatus { Pending, Approved, Rejected }

public enum ApprovalStepStatus { Waiting, Pending, Approved, Rejected, Delegated }

/// <summary>How an approver in a rule stage is determined.</summary>
public enum ApproverKind { User, DepartmentHead, EntityManager, GroupCFO, GroupCEO, Legal }

/// <summary>Group-level function of a user, used by the approval role resolvers.</summary>
public enum GroupFunction { None, GroupCFO, GroupCEO, Legal }

public enum ApprovalDecision { Approve, Reject }

public enum DeviationSeverity { High, Medium, Low, None }

/// <summary>Functional roles. Stored as an int bit mask; exposed to the API as a string array.</summary>
[Flags]
public enum AppRole
{
    None = 0,
    Reader = 1,
    ContractOwner = 2,
    Approver = 4,
    Admin = 8
}

public static class AppRoleExtensions
{
    private static readonly AppRole[] Single = { AppRole.Reader, AppRole.ContractOwner, AppRole.Approver, AppRole.Admin };

    public static List<string> ToNames(this AppRole roles)
    {
        var names = new List<string>();
        foreach (var role in Single)
        {
            if ((roles & role) == role)
            {
                names.Add(role.ToString());
            }
        }
        return names;
    }
}
