// -----------------------------------------------------------------------
//  <copyright file="ReadOnlyPutTrigger.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using Raven.Abstractions.Data;
using Raven.Bundles.Versioning;
using Raven.Json.Linq;

namespace Raven.Database.Plugins.Builtins
{
    public class ReadOnlyPutTrigger : AbstractPutTrigger
    {
        public override VetoResult AllowPut(string key, RavenJObject document, RavenJObject metadata)
        {
            var isNewReadOnly = metadata.Value<bool>(Constants.RavenReadOnly);
            if (!isNewReadOnly)
                return VetoResult.Allowed;

            var old = Database.Documents.Get(key);
            if (old == null)
                return VetoResult.Allowed;

            var isOldReadOnly = old.Metadata.Value<bool>(Constants.RavenReadOnly);
            if (isOldReadOnly)
            {
                if (Database.IsVersioningDisabledForImport(metadata))
                {
                    return VetoResult.Allowed;
                }
                return VetoResult.Deny(string.Format("You cannot update document '{0}' when both of them, new and existing one, are marked as readonly. To update this document change '{1}' flag to 'False' or remove it entirely.", key, Constants.RavenReadOnly));
            }
            return VetoResult.Allowed;
        }
    }
}
