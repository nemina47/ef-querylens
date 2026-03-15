namespace EFQueryLens.Core;

public enum QueryTranslationStatus
{
    Ready = 0,
    InQueue = 1,
    Starting = 2,
    Unreachable = 3,
}