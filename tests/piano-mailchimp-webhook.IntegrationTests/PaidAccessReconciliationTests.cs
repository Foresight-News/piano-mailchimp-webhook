using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;
using Xunit;

namespace piano_mailchimp_webhook.IntegrationTests;

public sealed class PaidAccessReconciliationTests
{
    [Fact]
    public async Task ReconciliationSkipsMembersWithoutPianoIdAndDryRunsExpiredAccess()
    {
        var mailchimp = new FakeMailchimpAudienceService(
            new MailchimpListMember
            {
                EmailAddress = "missing@example.com"
            },
            CreateMember("expired@example.com", "uid-expired"),
            CreateMember("active@example.com", "uid-active"));
        var piano = new FakePianoApiClient(new Dictionary<string, bool>
        {
            ["uid-expired"] = false,
            ["uid-active"] = true
        });

        var service = new PaidAccessReconciliationService(
            mailchimp,
            piano,
            Options.Create(new PaidAccessReconciliationOptions
            {
                PaidTagSegmentId = "paid-segment",
                PaidTagName = "PAID",
                ExpiredTagName = "EXPIRED",
                DryRun = true
            }),
            Options.Create(new SubscriberIdentityBackfillOptions()),
            NullLogger<PaidAccessReconciliationService>.Instance);

        var summary = await service.ReconcileAsync();

        Assert.Equal(3, summary.Scanned);
        Assert.Equal(1, summary.MissingPianoId);
        Assert.Equal(1, summary.WouldAddExpiredTag);
        Assert.Equal(0, summary.AddedExpiredTag);
        Assert.Equal(1, summary.ActiveAccess);
        Assert.Empty(mailchimp.AddedTags);
        Assert.Empty(mailchimp.RemovedTags);
    }

    [Fact]
    public async Task ReconciliationAddsExpiredTagWhenDryRunIsDisabled()
    {
        var mailchimp = new FakeMailchimpAudienceService(CreateMember("expired@example.com", "uid-expired"));
        var piano = new FakePianoApiClient(new Dictionary<string, bool>
        {
            ["uid-expired"] = false
        });

        var service = new PaidAccessReconciliationService(
            mailchimp,
            piano,
            Options.Create(new PaidAccessReconciliationOptions
            {
                PaidTagSegmentId = "paid-segment",
                PaidTagName = "PAID",
                ExpiredTagName = "EXPIRED",
                DryRun = false
            }),
            Options.Create(new SubscriberIdentityBackfillOptions()),
            NullLogger<PaidAccessReconciliationService>.Instance);

        var summary = await service.ReconcileAsync();

        Assert.Equal(1, summary.AddedExpiredTag);
        var addition = Assert.Single(mailchimp.AddedTags);
        Assert.Equal("expired@example.com", addition.Email);
        Assert.Equal("EXPIRED", Assert.Single(addition.Tags));
    }

    [Fact]
    public async Task BackfillDryRunReportsResolvableMissingPianoIdsWithoutUpdatingMailchimp()
    {
        var mailchimp = new FakeMailchimpAudienceService(
            new MailchimpListMember
            {
                EmailAddress = "missing@example.com"
            },
            CreateMember("existing@example.com", "uid-existing"),
            new MailchimpListMember
            {
                EmailAddress = "unknown@example.com"
            },
            new MailchimpListMember
            {
                EmailAddress = "ambiguous@example.com"
            });
        var resolver = new FakeSubscriberIdentityResolver(new Dictionary<string, SubscriberIdentityResolution>
        {
            ["missing@example.com"] = SubscriberIdentityResolution.Found("uid-missing"),
            ["ambiguous@example.com"] = SubscriberIdentityResolution.Ambiguous
        });

        var service = new SubscriberIdentityBackfillService(
            mailchimp,
            resolver,
            Options.Create(new PaidAccessReconciliationOptions
            {
                PaidTagSegmentId = "paid-segment",
                DryRun = true
            }),
            Options.Create(new SubscriberIdentityBackfillOptions
            {
                DryRun = true
            }),
            NullLogger<SubscriberIdentityBackfillService>.Instance);

        var summary = await service.BackfillAsync();

        Assert.Equal(4, summary.Scanned);
        Assert.Equal(1, summary.AlreadyHadPianoId);
        Assert.Equal(1, summary.WouldUpdate);
        Assert.Equal(1, summary.NotFound);
        Assert.Equal(1, summary.Ambiguous);
        Assert.Empty(mailchimp.MergeFieldUpdates);
    }

    [Fact]
    public async Task BackfillUpdatesPianoIdWhenDryRunIsDisabled()
    {
        var mailchimp = new FakeMailchimpAudienceService(new MailchimpListMember
        {
            EmailAddress = "missing@example.com"
        });
        var resolver = new FakeSubscriberIdentityResolver(new Dictionary<string, SubscriberIdentityResolution>
        {
            ["missing@example.com"] = SubscriberIdentityResolution.Found("uid-missing")
        });

        var service = new SubscriberIdentityBackfillService(
            mailchimp,
            resolver,
            Options.Create(new PaidAccessReconciliationOptions
            {
                PaidTagSegmentId = "paid-segment"
            }),
            Options.Create(new SubscriberIdentityBackfillOptions
            {
                DryRun = false
            }),
            NullLogger<SubscriberIdentityBackfillService>.Instance);

        var summary = await service.BackfillAsync();

        Assert.Equal(1, summary.Updated);
        var update = Assert.Single(mailchimp.MergeFieldUpdates);
        Assert.Equal("missing@example.com", update.Email);
        Assert.Equal("uid-missing", update.MergeFields["PIANOID"]);
    }

    private static MailchimpListMember CreateMember(string email, string pianoUid)
    {
        return new MailchimpListMember
        {
            EmailAddress = email,
            MergeFields = new Dictionary<string, JsonElement>
            {
                ["PIANOID"] = JsonSerializer.SerializeToElement(pianoUid)
            }
        };
    }

    private sealed class FakeMailchimpAudienceService(params MailchimpListMember[] members)
        : IMailchimpAudienceService
    {
        public List<(string Email, IReadOnlyDictionary<string, object?> MergeFields)> MergeFieldUpdates { get; } = [];

        public List<(string Email, IReadOnlyList<string> Tags)> AddedTags { get; } = [];

        public List<(string Email, IReadOnlyList<string> Tags)> RemovedTags { get; } = [];

        public Task<MailchimpListMembersPage> ListSegmentMembersAsync(
            string segmentId,
            int count,
            int offset,
            CancellationToken cancellationToken = default)
        {
            var pageMembers = members.Skip(offset).Take(count).ToList();
            return Task.FromResult(new MailchimpListMembersPage
            {
                Members = pageMembers,
                TotalItems = members.Length
            });
        }

        public Task UpsertMemberAsync(
            MailchimpMemberUpsertRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateMemberMergeFieldsAsync(
            string email,
            IReadOnlyDictionary<string, object?> mergeFields,
            CancellationToken cancellationToken = default)
        {
            MergeFieldUpdates.Add((email, mergeFields));
            return Task.CompletedTask;
        }

        public Task AddMemberTagsAsync(
            string email,
            IEnumerable<string> tags,
            CancellationToken cancellationToken = default)
        {
            AddedTags.Add((email, tags.ToList()));
            return Task.CompletedTask;
        }

        public Task RemoveMemberTagsAsync(
            string email,
            IEnumerable<string> tags,
            CancellationToken cancellationToken = default)
        {
            RemovedTags.Add((email, tags.ToList()));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePianoApiClient(IReadOnlyDictionary<string, bool> activeAccessByUid)
        : IPianoApiClient
    {
        public Task<PianoUserProfile?> GetUserAsync(
            string uid,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> HasActiveAccessToAnyResourceAsync(
            string uid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(activeAccessByUid[uid]);
        }
    }

    private sealed class FakeSubscriberIdentityResolver(
        IReadOnlyDictionary<string, SubscriberIdentityResolution> resolutions) : ISubscriberIdentityResolver
    {
        public Task<SubscriberIdentityResolution> ResolveAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                resolutions.TryGetValue(email, out var resolution)
                    ? resolution
                    : SubscriberIdentityResolution.NotFound);
        }
    }
}
