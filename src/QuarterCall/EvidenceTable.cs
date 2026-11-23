namespace QuarterCall;

/// <summary>One of the files the host publishes for a quarter, by the manifest member that names its path.</summary>
public enum EvidenceTable
{
    /// <summary>The account values filed for the quarter: the file in the directory <c>accounts_path</c>.</summary>
    Accounts,

    /// <summary>
    /// The text values filed for the quarter: the file in the directory <c>accounts_text_path</c>. A quarter
    /// without text values has none.
    /// </summary>
    AccountText,

    /// <summary>A profile of each filing in the quarter: the file in the directory <c>profiles_path</c>.</summary>
    Profiles,

    /// <summary>The detail of the quarter's filing quorum: the file <c>quorum.detail_path</c>.</summary>
    Quorum,
}
