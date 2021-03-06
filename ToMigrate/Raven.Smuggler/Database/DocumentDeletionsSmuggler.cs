// -----------------------------------------------------------------------
//  <copyright file="DeletionsSmuggler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Database.Smuggler.Database;

namespace Raven.Smuggler.Database
{
    internal class DocumentDeletionsSmuggler : SmugglerBase
    {
        private readonly DatabaseLastEtagsInfo _maxEtags;

        public DocumentDeletionsSmuggler(DatabaseSmugglerOptions options, DatabaseSmugglerNotifications notifications, IDatabaseSmugglerSource source, IDatabaseSmugglerDestination destination, DatabaseLastEtagsInfo maxEtags)
            : base(options, notifications, source, destination)
        {
            _maxEtags = maxEtags;
        }

        public override async Task SmuggleAsync(DatabaseSmugglerOperationState state, CancellationToken cancellationToken)
        {
            using (var actions = Destination.DocumentDeletionActions())
            {
                if (Source.SupportsDocumentDeletions == false)
                    return;

                if (Options.OperateOnTypes.HasFlag(DatabaseItemType.Documents) == false)
                {
                    await Source.SkipDocumentDeletionsAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var maxEtag = _maxEtags.LastDocDeleteEtag?.IncrementBy(1);

                List<KeyValuePair<string, Etag>> deletions = null;
                try
                {
                    deletions = await Source
                        .ReadDocumentDeletionsAsync(state.LastDocDeleteEtag, maxEtag, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (Options.IgnoreErrorsAndContinue == false)
                        throw;

                    Notifications.ShowProgress(DatabaseSmugglerMessages.DocumentDeletions_Read_Failure, e.Message);
                }

                if (deletions == null || deletions.Count == 0)
                    return;

                var lastEtag = state.LastDocDeleteEtag;

                foreach (var deletion in deletions)
                {
                    Notifications.OnDocumentDeletionRead(this, deletion.Key);

                    try
                    {
                        await actions
                            .WriteDocumentDeletionAsync(deletion.Key, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        if (Options.IgnoreErrorsAndContinue == false)
                            throw;

                        Notifications.ShowProgress(DatabaseSmugglerMessages.DocumentDeletions_Write_Failure, deletion.Key, e.Message);
                    }

                    Notifications.OnDocumentDeletionWrite(this, deletion.Key);

                    lastEtag = deletion.Value;
                }

                state.LastDocDeleteEtag = lastEtag;
            }
        }
    }
}
