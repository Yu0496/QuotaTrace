namespace UsageTray.Core;

public enum DataQuality
{
    Exact = 0,
    Derived = 1,
    QuotaOnly = 2,
    Unavailable = 3
}

public enum CostQuality
{
    ExactTokenSplit = 0,
    ExactTokensNoCache = 1,
    PartialPrice = 2,
    Unavailable = 3
}
