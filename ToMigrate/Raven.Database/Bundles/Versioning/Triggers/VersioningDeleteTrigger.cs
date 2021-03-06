//-----------------------------------------------------------------------
// <copyright file="VersioningDeleteTrigger.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
// using Microsoft.VisualBasic.Logging;
using Raven.Abstractions.Data;
using Raven.Bundles.Versioning.Data;
using Raven.Database.Plugins;
using Raven.Json.Linq;
using System.Linq;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Threading;

namespace Raven.Bundles.Versioning.Triggers
{
    [InheritedExport(typeof(AbstractDeleteTrigger))]
    [ExportMetadata("Bundle", "Versioning")]
    public class VersioningDeleteTrigger : AbstractDeleteTrigger
    {
        readonly Raven.Abstractions.Threading.ThreadLocal<Dictionary<string, RavenJObject>> versionInformer 
            = new Raven.Abstractions.Threading.ThreadLocal<Dictionary<string, RavenJObject>>(() => new Dictionary<string, RavenJObject>());

        public override VetoResult AllowDelete(string key)
        {
            var document = Database.Documents.Get(key);
            if (document == null)
                return VetoResult.Allowed;

            versionInformer.Value[key] = document.Metadata;
            if (document.Metadata.Value<string>(VersioningUtil.RavenDocumentRevisionStatus) != "Historical")
                return VetoResult.Allowed;

            if (Database.Configuration.Versioning.ChangesToRevisionsAllowed == false &&
                Database.IsVersioningActive(document.Metadata))
            {
                var revisionPos = key.LastIndexOf("/revisions/", StringComparison.OrdinalIgnoreCase);
                if (revisionPos != -1)
                {
                    var parentKey = key.Remove(revisionPos);
                    var parentDoc = Database.Documents.Get(parentKey);
                    if (parentDoc == null)
                        return VetoResult.Allowed;
                }

                return VetoResult.Deny("Deleting a historical revision is not allowed");
            }

            return VetoResult.Allowed;
        }

        public override void AfterDelete(string key)
        {
            var versioningConfig = Database.GetDocumentVersioningConfiguration(versionInformer.Value[key]);
    
            
            using (Database.DisableAllTriggersForCurrentThread())
            {
                Database.TransactionalStorage.Batch(accessor =>
                {
                    foreach (var jsonDocument in  accessor.Documents.GetDocumentsWithIdStartingWith(key + "/revisions/", 0, int.MaxValue, null))
                    {
                        if(jsonDocument == null)
                            continue;
                        if (versioningConfig != null && versioningConfig.PurgeOnDelete)
                        {
                            Database.Documents.Delete(jsonDocument.Key, null);
                        }
                        else
                        {
                            jsonDocument.Metadata.Remove(Constants.RavenReadOnly);
                            accessor.Documents.AddDocument(jsonDocument.Key, jsonDocument.Etag, jsonDocument.DataAsJson, jsonDocument.Metadata);
                        }
                    }

                
                });
            }
        }
    }
}
