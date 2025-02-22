﻿using System;
using System.Runtime.Serialization;
namespace Kiota.Builder.SearchProviders.GitHub.GitHubClient.Models
{
    /// <summary>The level of permission to grant the access token to manage the post-receive hooks for a repository.</summary>
    public enum AppPermissions_repository_hooks
    {
        [EnumMember(Value = "read")]
        Read,
        [EnumMember(Value = "write")]
        Write,
    }
}
