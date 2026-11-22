namespace Cabinet.Diagnostic.Core;

public enum AntivirusState { Unknown, ActiveUpToDate, ActiveOutdated, Disabled, None }

public enum ObjectModelGuardPolicy { NotSet, WarnWhenAntivirusInactive, AlwaysWarn, NeverWarn }

public enum CloudSync { None, OneDrive, OtherService }
