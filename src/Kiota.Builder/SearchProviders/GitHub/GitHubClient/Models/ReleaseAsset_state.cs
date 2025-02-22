﻿using System;
using System.Runtime.Serialization;
namespace Kiota.Builder.SearchProviders.GitHub.GitHubClient.Models
{
    /// <summary>State of the release asset.</summary>
    public enum ReleaseAsset_state
    {
        [EnumMember(Value = "uploaded")]
        Uploaded,
        [EnumMember(Value = "open")]
        Open,
    }
}
