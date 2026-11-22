using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Tests;

/// <summary>Release 4 e-signature at service level with the Simulated provider (CONTRACT_V4 section 8).</summary>
public class Release4SignatureTests
{
    private static Contract Reload(TestWorld world, Guid id) => ReviewTestHelpers.Reload(world, id);

    // ---------- settings ----------

    [Fact]
    public async Task Settings_AreValidated_AndAProviderWithoutServerConfigurationCannotBeChosen()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var defaults = await admin.Signatures.GetSettingsAsync();
        Assert.Equal("None", defaults.Provider);
        Assert.True(defaults.RequireApproved);
        Assert.Equal(30, defaults.ExpiryDays);

        // Only the Simulated provider is registered in this world: DocuSign and Penneo are "not configured".
        foreach (var provider in new[] { "DocuSign", "Penneo", "Adobe" })
        {
            var error = await Assert.ThrowsAsync<InputValidationException>(() => admin.Signatures.UpdateSettingsAsync(new ESignSettingsDto { Provider = provider }));
            Assert.True(error.Errors.ContainsKey("provider"), provider);
        }
        var bad = await Assert.ThrowsAsync<InputValidationException>(() => admin.Signatures.UpdateSettingsAsync(new ESignSettingsDto
        {
            Provider = "Simulated", DefaultOrder = "Random", ReminderDays = new List<int> { 0, 90 }, ExpiryDays = 400,
            AuthorisedSignatories = new List<AuthorisedSignatoriesDto> { new() { EntityId = Guid.NewGuid(), UserIds = new List<Guid> { world.Cfo } } }
        }));
        foreach (var field in new[] { "defaultOrder", "reminderDays", "expiryDays", "authorisedSignatories" })
        {
            Assert.True(bad.Errors.ContainsKey(field), field);
        }

        var saved = await admin.Signatures.UpdateSettingsAsync(new ESignSettingsDto
        {
            Provider = "Simulated", DefaultMessage = "Sign here, please.", DefaultOrder = "Sequential", ReminderDays = new List<int> { 7, 3 }, ExpiryDays = 14,
            RequireApproved = true, RestrictToAuthorised = true,
            AuthorisedSignatories = new List<AuthorisedSignatoriesDto> { new() { EntityId = world.EntityA, UserIds = new List<Guid> { world.Cfo }, ExternalNames = new List<string> { " Board Chair " } } }
        });
        Assert.Equal("Simulated", saved.Provider);
        Assert.Equal(new List<int> { 3, 7 }, saved.ReminderDays);
        Assert.Equal("Board Chair", saved.AuthorisedSignatories.Single().ExternalNames.Single());
        Assert.NotNull(saved.ProviderAccount);

        // The general feature toggle and app-config mirror it.
        Assert.True((await admin.Settings.GetAppSettingsAsync()).Features.ESign);
        var config = await world.As(world.Outsider).Personal.GetAppConfigAsync();
        Assert.Equal("Simulated", config.ESign.Provider);
        Assert.True(config.ESign.Enabled);

        using var db = world.NewDb();
        Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "settings.esign"));
    }

    [Fact]
    public async Task SimulatedProvider_IsRefused_WhereSimulationIsNotAllowed()
    {
        // Outside Dev sign-in the simulated provider is off unless Signing:AllowSimulated says otherwise.
        var production = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "Entra" }).Build();
        Assert.False(new Dafa.Contracts.Infrastructure.Providers.SimulatedSignatureProvider(production).IsConfigured);
        var optedIn = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "Entra", ["Signing:AllowSimulated"] = "true" }).Build();
        Assert.True(new Dafa.Contracts.Infrastructure.Providers.SimulatedSignatureProvider(optedIn).IsConfigured);
        var switchedOff = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "Dev", ["Signing:AllowSimulated"] = "false" }).Build();
        Assert.False(new Dafa.Contracts.Infrastructure.Providers.SimulatedSignatureProvider(switchedOff).IsConfigured);

        // DocuSign and Penneo read their credentials from server configuration only.
        var factory = new StaticHttpClientFactory(new ScriptedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        Assert.False(new Dafa.Contracts.Infrastructure.Providers.DocuSignSignatureProvider(production, factory).IsConfigured);
        Assert.False(new Dafa.Contracts.Infrastructure.Providers.PenneoSignatureProvider(production, factory).IsConfigured);
        var penneo = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Signing:Penneo:BaseUrl"] = "https://sandbox.penneo.com/api/v3", ["Signing:Penneo:ApiKey"] = "key-123456", ["Signing:Penneo:ApiSecret"] = "secret"
        }).Build();
        var configured = new Dafa.Contracts.Infrastructure.Providers.PenneoSignatureProvider(penneo, factory);
        Assert.True(configured.IsConfigured);
        Assert.DoesNotContain("secret", configured.Account ?? "");

        using var world = new TestWorld();
        var contractAndDocument = await R4.SigningSetupAsync(world);
        // The provider was chosen, then simulation is switched off on the server: sending and simulating stop.
        var registry = new SignatureProviderRegistry(new ISignatureProvider[] { new Dafa.Contracts.Infrastructure.Providers.SimulatedSignatureProvider(switchedOff) });
        var owner = world.As(world.Owner);
        var service = new SignatureService(owner.Db, new FakeCurrentUser(owner.Db, world.Owner, "Owner User"), owner.Access, world.Clock,
            owner.Audit, new DocumentWriter(owner.Db, world.Store, world.Clock), world.Store, owner.Dispatcher, registry);
        var refused = await Assert.ThrowsAsync<FeatureOffException>(() => service.CreateAndSendAsync(contractAndDocument.Contract.Id, R4.TwoSigners(world, contractAndDocument.DocumentId)));
        Assert.Equal(ProblemTypesV3.FeatureOff, refused.ProblemType);
    }

    // ---------- permission ----------

    [Fact]
    public async Task OnlyPermittedUsers_WithinTheirEntityLimit_MaySend()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        var input = R4.TwoSigners(world, documentId);

        // An administrator does not get the permission implicitly.
        var asAdmin = await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Admin).Signatures.CreateAndSendAsync(contract.Id, input));
        Assert.Equal(403, asAdmin.StatusCode);
        Assert.Equal(ProblemTypesV4.NotPermitted, asAdmin.ProblemType);
        // A department member who can see the contract but has no permission.
        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.PurchasingMember).Signatures.CreateAndSendAsync(contract.Id, input));
        // Someone who cannot even see the contract gets the access answer, not a hint about signing.
        world.UpdateUser(world.Outsider, u => u.CanSendForSignature = true);
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Signatures.CreateAndSendAsync(contract.Id, input));

        // The owner has the permission, but limited to Entity B: this contract is in Entity A.
        world.UpdateUser(world.Owner, u => u.SignatureEntityIdsJson = JsonDefaults.Write(new List<Guid> { world.EntityB }));
        var limited = await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, input));
        Assert.Contains("Entity A", limited.Message);
        var permissions = await world.As(world.Owner).Signatures.GetPermissionsAsync(contract.Id);
        Assert.False(permissions.CanSend);
        Assert.NotNull(permissions.Reason);
        Assert.False((await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false)).CanSendForSignature);

        // A colleague who has the permission for Entity A and can see the contract is offered ("ask a colleague").
        world.UpdateUser(world.PurchasingHead, u => { u.CanSendForSignature = true; u.SignatureEntityIdsJson = JsonDefaults.Write(new List<Guid> { world.EntityA }); });
        permissions = await world.As(world.Owner).Signatures.GetPermissionsAsync(contract.Id);
        Assert.Equal(world.PurchasingHead, Assert.Single(permissions.PermittedUsers).Id);      // not the outsider: they cannot see it

        // With the limit including Entity A the owner may send.
        world.UpdateUser(world.Owner, u => u.SignatureEntityIdsJson = JsonDefaults.Write(new List<Guid> { world.EntityA, world.EntityB }));
        Assert.True((await world.As(world.Owner).Signatures.GetPermissionsAsync(contract.Id)).CanSend);
        Assert.True((await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false)).CanSendForSignature);
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, input);
        Assert.Equal("Sent", sent.Status);

        using var db = world.NewDb();
        Assert.Equal(1, db.SignatureRequests.Count());
    }

    [Fact]
    public async Task ContractMustBeApproved_WhenTheSettingsRequireIt()
    {
        using var world = new TestWorld();
        var (approved, documentId) = await R4.SigningSetupAsync(world);
        var draft = world.AddContract(c => c.Status = ContractStatus.Draft);
        var draftDocument = await world.As(world.Owner).Documents.UploadAsync(draft.Id, "Draft.pdf", "Main", new MemoryStream(ReviewTestHelpers.PdfBytes()));
        var inApproval = world.AddContract(c => c.Status = ContractStatus.InApproval);
        var owner = world.As(world.Owner);
        var draftInput = R4.TwoSigners(world, draftDocument.Id);

        var notApproved = await Assert.ThrowsAsync<NotPermittedException>(() => owner.Signatures.CreateAndSendAsync(draft.Id, draftInput));
        Assert.Contains("approved", notApproved.Message);
        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(inApproval.Id, draftInput));

        // Approval not required: a draft may be sent, a contract in approval still may not.
        await world.As(world.Admin).Signatures.UpdateSettingsAsync(new ESignSettingsDto { Provider = "Simulated", RequireApproved = false });
        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(inApproval.Id, draftInput));
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(draft.Id, draftInput);
        foreach (var signer in sent.Signers)
        {
            await world.As(world.Admin).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = signer.Id, Action = "Sign" });
        }
        // Signed, but a draft does not become active by signing: it still has to be approved.
        var stored = Reload(world, draft.Id);
        Assert.Equal(ContractStatus.Draft, stored.Status);
        Assert.Equal(world.Clock.Today, stored.SignedDate);
        Assert.Equal(ContractStatus.Approved, Reload(world, approved.Id).Status);
        Assert.NotEqual(documentId, draftDocument.Id);

        // Provider None: every send route is 400 feature-off.
        await world.As(world.Admin).Signatures.UpdateSettingsAsync(new ESignSettingsDto { Provider = "None" });
        await Assert.ThrowsAsync<FeatureOffException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(approved.Id, R4.TwoSigners(world, documentId)));
    }

    [Fact]
    public async Task Input_IsValidated_Signers_Emails_Documents_AndAuthorisedSignatories()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        var other = world.AddContract();
        var foreign = await world.As(world.Owner).Documents.UploadAsync(other.Id, "Other.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("x")));
        var owner = world.As(world.Owner);

        async Task<IDictionary<string, string[]>> ErrorsAsync(SignatureRequestInput input) =>
            (await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, input))).Errors;

        Assert.True((await ErrorsAsync(new SignatureRequestInput { DocumentIds = new List<Guid>(), Signers = R4.TwoSigners(world, documentId).Signers })).ContainsKey("documentIds"));
        Assert.True((await ErrorsAsync(new SignatureRequestInput { DocumentIds = new List<Guid> { foreign.Id }, Signers = R4.TwoSigners(world, documentId).Signers })).ContainsKey("documentIds"));
        Assert.True((await ErrorsAsync(new SignatureRequestInput { DocumentIds = new List<Guid> { documentId }, Signers = new List<SignerInput>() })).ContainsKey("signers"));

        foreach (var email in new[] { "", "not-an-email", "a@b", "two@@example.com", "spaced name@example.com", "x@example.com\r\nBcc: other@example.com", "<x@example.com>" })
        {
            var errors = await ErrorsAsync(new SignatureRequestInput
            {
                DocumentIds = new List<Guid> { documentId },
                Signers = new List<SignerInput> { new() { Kind = "External", Name = "Anna", Email = email, Order = 1 } }
            });
            Assert.True(errors.ContainsKey("signers[0].email"), "accepted: " + email);
        }
        Assert.False(SignatureService.IsValidEmail("x@example.com\nBcc: y@example.com"));
        Assert.True(SignatureService.IsValidEmail("anna.lindqvist+contracts@supplier-group.example"));

        world.UpdateUser(world.Deputy, u => u.IsActive = false);
        var people = await ErrorsAsync(new SignatureRequestInput
        {
            DocumentIds = new List<Guid> { documentId },
            Order = "Zigzag",
            Signers = new List<SignerInput>
            {
                new() { Kind = "Internal", UserId = world.Deputy, Order = 1 },
                new() { Kind = "External", Name = "", Email = "ok@example.com", Order = 2 },
                new() { Kind = "Robot", Order = 3 }
            }
        });
        Assert.True(people.ContainsKey("signers[0].userId"));
        Assert.True(people.ContainsKey("signers[1].name"));
        Assert.True(people.ContainsKey("signers[2].kind"));
        Assert.True(people.ContainsKey("order"));

        var twice = await ErrorsAsync(new SignatureRequestInput
        {
            DocumentIds = new List<Guid> { documentId },
            Signers = new List<SignerInput>
            {
                new() { Kind = "External", Name = "A", Email = "same@example.com", Order = 1 },
                new() { Kind = "External", Name = "B", Email = "SAME@example.com", Order = 2 }
            }
        });
        Assert.True(twice.ContainsKey("signers"));

        // Restricted to authorised signatories of the contract's legal entity.
        await world.As(world.Admin).Signatures.UpdateSettingsAsync(new ESignSettingsDto
        {
            Provider = "Simulated", RestrictToAuthorised = true,
            AuthorisedSignatories = new List<AuthorisedSignatoriesDto> { new() { EntityId = world.EntityA, UserIds = new List<Guid> { world.Ceo } } }
        });
        Assert.True((await ErrorsAsync(R4.TwoSigners(world, documentId))).ContainsKey("signers"));       // the CFO is not authorised for Entity A
        var withCeo = R4.TwoSigners(world, documentId);
        withCeo.Signers![0].UserId = world.Ceo;
        Assert.Equal("Sent", (await owner.Signatures.CreateAndSendAsync(contract.Id, withCeo)).Status);
        using var db = world.NewDb();
        Assert.Equal(1, db.SignatureRequests.Count());       // none of the refused inputs left anything behind
    }

    // ---------- the flow ----------

    [Fact]
    public async Task SimulatedFlow_Parallel_SendsViewsSigns_AndCompletesToActive()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        await world.As(world.PurchasingMember).Collaboration.WatchAsync(contract.Id, true);

        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId));

        Assert.Equal("Sent", sent.Status);
        Assert.Equal("Simulated", sent.Provider);
        Assert.Equal("Parallel", sent.Order);
        Assert.Equal("Owner User", sent.CreatedBy);
        Assert.NotNull(sent.ExpiresAt);
        Assert.Equal(new[] { "Sent", "Sent" }, sent.Signers.Select(s => s.Status).ToArray());
        Assert.Equal(new[] { "Internal", "External" }, sent.Signers.Select(s => s.Kind).ToArray());
        Assert.Equal("cfo.user@example.test", sent.Signers[0].Email);
        using (var db = world.NewDb())
        {
            // The internal signer is told; the external one only hears from the provider.
            Assert.Equal(1, db.Notifications.Count(n => n.Event == "SignatureRequested" && n.UserId == world.Cfo));
            Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "SignatureRequested"));
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "signature.sent" && e.ContractId == contract.Id));
            Assert.StartsWith("SIM-", db.SignatureRequests.Single().ProviderReference);
        }

        // One open request per contract.
        var second = await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId)));
        Assert.Equal(ProblemTypesV4.SignatureState, second.ProblemType);
        // The manual path is closed while a request is open.
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.SignAsync(contract.Id, new SignInput { SignedDate = world.Clock.Today }));

        var internalSigner = sent.Signers[0];
        var externalSigner = sent.Signers[1];
        var admin = world.As(world.Admin);
        var viewed = await admin.Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = externalSigner.Id, Action = "View" });
        Assert.Equal("Viewed", viewed.Signers[1].Status);
        Assert.NotNull(viewed.Signers[1].At);

        var partly = await world.As(world.Admin).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = externalSigner.Id, Action = "Sign" });
        Assert.Equal("Sent", partly.Status);                       // partially signed is still "Sent" in the API
        Assert.Equal(1, partly.SignedCount);
        Assert.Equal(ContractStatus.Approved, Reload(world, contract.Id).Status);
        using (var db = world.NewDb())
        {
            Assert.Equal(SignatureRequestStatus.PartiallySigned, db.SignatureRequests.Single().Status);
        }
        // A signer signs once.
        var again = await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Admin).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = externalSigner.Id, Action = "Sign" }));
        Assert.Equal(ProblemTypesV4.SignatureState, again.ProblemType);

        var done = await world.As(world.Admin).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = internalSigner.Id, Action = "Sign" });

        Assert.Equal("Completed", done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.NotNull(done.SignedDocumentId);
        Assert.All(done.Signers, s => Assert.Equal("Signed", s.Status));
        var stored = Reload(world, contract.Id);
        Assert.Equal(ContractStatus.Active, stored.Status);
        Assert.Equal(world.Clock.Today, stored.SignedDate);
        Assert.Equal(SignatureService.SigningMethod, stored.SigningMethod);
        using (var db = world.NewDb())
        {
            var signed = db.Documents.Single(d => d.Id == done.SignedDocumentId);
            Assert.Equal(DocumentKind.Main, signed.Kind);
            Assert.Equal("Agreement – signed.pdf", signed.Name);
            Assert.NotNull(signed.ContentHash);
            var bytes = world.Store.Files[signed.StoreKey];
            var text = Encoding.ASCII.GetString(bytes);
            Assert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);
            Assert.Contains("SIMULATED SIGNATURE", text);
            Assert.Contains("Anna Lindqvist <anna@supplier.example>", text);
            Assert.Contains(SignatureService.Sha256Hex(ReviewTestHelpers.PdfBytes("agreement")), text);
            Assert.True(FileTypeRules.ContentMatches("signed.pdf", bytes));

            foreach (var action in new[] { "signature.viewed", "signature.completed", "contract.signed" })
            {
                Assert.Equal(1, db.AuditEvents.Count(e => e.Action == action && e.ContractId == contract.Id));
            }
            Assert.Equal(2, db.AuditEvents.Count(e => e.Action == "signature.signed" && e.ContractId == contract.Id));
            // The owner (who is also the sender) and the watcher are notified.
            Assert.Equal(1, db.Notifications.Count(n => n.Event == "SignatureCompleted" && n.UserId == world.Owner));
            Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "SignatureCompleted" && m.RecipientId == world.Owner));
            Assert.True(db.Notifications.Any(n => n.UserId == world.PurchasingMember && n.ContractId == contract.Id
                && (n.Event == "SignatureCompleted" || n.Event == "WatchedContractChanged")));
        }
        var detail = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.Equal("Completed", detail.Signature!.Status);
        Assert.True(detail.HasSignedDocument);
        Assert.False(detail.CanSendForSignature);          // active now, and approval is required
        Assert.Single(await world.As(world.PurchasingMember).Signatures.ListAsync(contract.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Signatures.ListAsync(contract.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Signatures.GetAsync(sent.Id));
        // A finished request cannot be changed any more.
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Signatures.CancelAsync(sent.Id, null));
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Signatures.ResendAsync(sent.Id));
    }

    [Fact]
    public async Task Sequential_OnlyTheCurrentSignerMayAct_AndTheNextIsInvitedAfterwards()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId, "Sequential"));
        Assert.Equal("Sequential", sent.Order);
        Assert.Equal(new[] { "Sent", "Waiting" }, sent.Signers.Select(s => s.Status).ToArray());

        var early = await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Owner).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = sent.Signers[1].Id, Action = "Sign" }));
        Assert.Equal(ProblemTypesV4.SignatureState, early.ProblemType);

        var afterFirst = await world.As(world.Owner).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = sent.Signers[0].Id, Action = "Sign" });
        Assert.Equal(new[] { "Signed", "Sent" }, afterFirst.Signers.Select(s => s.Status).ToArray());
        var done = await world.As(world.Owner).Signatures.SimulateAsync(sent.Id, new SimulateSignatureInput { SignerId = sent.Signers[1].Id, Action = "Sign" });
        Assert.Equal("Completed", done.Status);
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);

        // Only the sender, permitted users and administrators may simulate; and only with valid input.
        var another = world.AddContract(c => c.Status = ContractStatus.Approved);
        var document = await world.As(world.Admin).Documents.UploadAsync(another.Id, "B.pdf", "Main", new MemoryStream(ReviewTestHelpers.PdfBytes()));
        var open = await world.As(world.Owner).Signatures.CreateAndSendAsync(another.Id, R4.TwoSigners(world, document.Id));
        await Assert.ThrowsAsync<NotPermittedException>(
            () => world.As(world.PurchasingMember).Signatures.SimulateAsync(open.Id, new SimulateSignatureInput { SignerId = open.Signers[0].Id, Action = "Sign" }));
        await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Signatures.SimulateAsync(open.Id, new SimulateSignatureInput { SignerId = open.Signers[0].Id, Action = "Shred" }));
        await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Signatures.SimulateAsync(open.Id, new SimulateSignatureInput { SignerId = sent.Signers[0].Id, Action = "Sign" }));
    }

    [Fact]
    public async Task Declined_EndsTheRequest_LeavesTheContract_AndNotifiesTheSender()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId));

        var declined = await world.As(world.Admin).Signatures.SimulateAsync(
            sent.Id, new SimulateSignatureInput { SignerId = sent.Signers[1].Id, Action = "Decline", Reason = "The liability cap is wrong" });

        Assert.Equal("Declined", declined.Status);
        Assert.Equal("Declined", declined.Signers[1].Status);
        Assert.Equal("The liability cap is wrong", declined.Signers[1].DeclineReason);
        var stored = Reload(world, contract.Id);
        Assert.Equal(ContractStatus.Approved, stored.Status);
        Assert.Null(stored.SignedDate);
        using (var db = world.NewDb())
        {
            Assert.Equal(1, db.Notifications.Count(n => n.Event == "SignatureDeclined" && n.UserId == world.Owner));
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "signature.declined" && e.ContractId == contract.Id));
            Assert.Equal(1, db.Documents.Count(d => d.ContractId == contract.Id));       // no signed copy
        }
        // A new request can be sent, and the manual path is open again.
        Assert.True((await world.As(world.Owner).Signatures.GetPermissionsAsync(contract.Id)).CanSend);
        await world.As(world.Owner).Commands.SignAsync(contract.Id, new SignInput { SignedDate = world.Clock.Today, Method = "Wet ink" });
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);
        Assert.Equal("Wet ink", Reload(world, contract.Id).SigningMethod);
    }

    [Fact]
    public async Task Cancel_Resend_Reminders_AndExpiry()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world, s => { s.ReminderDays = new List<int> { 3 }; s.ExpiryDays = 10; });
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId));

        // Resend: the sender may; a colleague without the permission may not.
        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.PurchasingMember).Signatures.ResendAsync(sent.Id));
        await world.As(world.Owner).Signatures.ResendAsync(sent.Id);
        using (var db = world.NewDb())
        {
            Assert.Equal(2, db.Notifications.Count(n => n.Event == "SignatureRequested" && n.UserId == world.Cfo));
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "signature.resent"));
        }

        // The job: nothing is due after one day; the reminder goes out after three; the request expires after ten.
        world.Clock.AdvanceDays(1);
        var quiet = await world.As(null).Signatures.SyncAsync();
        Assert.Equal(1, quiet.Checked);
        Assert.Equal(0, quiet.Reminded);
        world.Clock.AdvanceDays(3);
        Assert.Equal(1, (await world.As(null).Signatures.SyncAsync()).Reminded);
        Assert.Equal(0, (await world.As(null).Signatures.SyncAsync()).Reminded);       // once per reminder day
        world.Clock.AdvanceDays(7);
        var expired = await world.As(null).Signatures.SyncAsync();
        Assert.Equal(1, expired.Expired);
        Assert.Empty(expired.Errors);
        var afterExpiry = await world.As(world.Owner).Signatures.GetAsync(sent.Id);
        Assert.Equal("Expired", afterExpiry.Status);
        Assert.All(afterExpiry.Signers, s => Assert.Equal("Expired", s.Status));
        Assert.Equal(ContractStatus.Approved, Reload(world, contract.Id).Status);
        using (var db = world.NewDb())
        {
            Assert.Equal(1, db.Notifications.Count(n => n.Event == "SignatureDeclined" && n.UserId == world.Owner));
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "signature.expired"));
        }
        Assert.Equal(0, (await world.As(null).Signatures.SyncAsync()).Checked);

        // Cancel: a new request, cancelled by the sender; it cannot be simulated afterwards.
        var next = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId));
        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.PurchasingMember).Signatures.CancelAsync(next.Id, null));
        var cancelled = await world.As(world.Owner).Signatures.CancelAsync(next.Id, "Wrong appendix");
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Wrong appendix", cancelled.StatusMessage);
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Owner).Signatures.SimulateAsync(next.Id, new SimulateSignatureInput { SignerId = next.Signers[0].Id, Action = "Sign" }));
        Assert.Equal(new[] { "Cancelled", "Expired" }, (await world.As(world.Owner).Signatures.ListAsync(contract.Id)).Select(r => r.Status).ToArray());
    }

    [Fact]
    public async Task Webhook_Events_AreApplied_Idempotently_AndUnknownReferencesAreIgnored()
    {
        using var world = new TestWorld();
        var (contract, documentId) = await R4.SigningSetupAsync(world);
        var sent = await world.As(world.Owner).Signatures.CreateAndSendAsync(contract.Id, R4.TwoSigners(world, documentId));
        string reference;
        using (var db = world.NewDb())
        {
            reference = db.SignatureRequests.Single().ProviderReference!;
        }
        var system = world.As(null);

        Assert.Equal(0, await system.Signatures.HandleWebhookAsync("Simulated", "{\"reference\":\"SIM-unknown\",\"event\":\"Completed\"}"));
        Assert.Equal(0, await world.As(null).Signatures.HandleWebhookAsync("Simulated", "this is not json"));
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(null).Signatures.HandleWebhookAsync("Adobe", "{}"));

        var signedByAnna = "{\"reference\":\"" + reference + "\",\"event\":\"Signed\",\"signerEmail\":\"ANNA@supplier.example\",\"at\":\"2026-03-03T10:15:00Z\"}";
        Assert.Equal(1, await world.As(null).Signatures.HandleWebhookAsync("Simulated", signedByAnna));
        Assert.Equal(0, await world.As(null).Signatures.HandleWebhookAsync("Simulated", signedByAnna));       // delivered twice: nothing changes
        var partly = await world.As(world.Owner).Signatures.GetAsync(sent.Id);
        Assert.Equal(1, partly.SignedCount);
        Assert.Equal(new DateTime(2026, 3, 3, 10, 15, 0, DateTimeKind.Utc), partly.Signers[1].At);

        var batch = "{\"reference\":\"" + reference + "\",\"events\":[{\"event\":\"Viewed\",\"signerId\":\"" + sent.Signers[0].Id + "\"},{\"event\":\"Signed\",\"signerId\":\"" + sent.Signers[0].Id + "\"}]}";
        Assert.Equal(2, await world.As(null).Signatures.HandleWebhookAsync("Simulated", batch));
        Assert.Equal("Completed", (await world.As(world.Owner).Signatures.GetAsync(sent.Id)).Status);
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);
        // After completion further events are ignored.
        Assert.Equal(0, await world.As(null).Signatures.HandleWebhookAsync("Simulated", "{\"reference\":\"" + reference + "\",\"event\":\"Declined\"}"));
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);
        using var check = world.NewDb();
        Assert.Contains(check.AuditEvents.Where(e => e.Action == "signature.completed").Select(e => e.UserName).ToList(), name => name == "System");
    }

    [Fact]
    public async Task ManualSignature_StaysForOwnerDelegateAndAdmin_Only()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Approved;
            c.DelegateId = world.PurchasingHead;        // has the ContractOwner role
            c.ResponsibleId = world.ItHead;             // has the ContractOwner role too, but is only responsible
        });
        var input = new SignInput { SignedDate = world.Clock.Today, Method = "Manual" };

        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.ItHead).Commands.SignAsync(contract.Id, input));
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.PurchasingMember).Commands.SignAsync(contract.Id, input));
        Assert.Equal(ContractStatus.Approved, Reload(world, contract.Id).Status);

        await world.As(world.PurchasingHead).Commands.SignAsync(contract.Id, input);
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);

        var second = world.AddContract(c => c.Status = ContractStatus.Approved);
        await world.As(world.Admin).Commands.SignAsync(second.Id, input);
        Assert.Equal(ContractStatus.Active, Reload(world, second.Id).Status);
    }

    [Fact]
    public void SimplePdf_IsAWellFormedPdf_WithCorrectOffsets()
    {
        var lines = Enumerable.Range(1, 120).Select(i => "Line " + i + " with (parentheses), a back\\slash and æøå – dash").ToList();
        var bytes = SimplePdf.FromLines("Signature page", lines);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.All(bytes, b => Assert.True(b < 128));
        Assert.Contains("/Count 3", text);                         // 120 lines at 48 per page
        Assert.Contains("\\(parentheses\\)", text);
        Assert.Contains("aeoeaa - dash", text);

        // startxref points at the xref table, and every xref entry points at "<n> 0 obj".
        var startXref = int.Parse(text.Substring(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10).Split('\n')[0]);
        Assert.Equal("xref", text.Substring(startXref, 4));
        var entries = text.Substring(startXref).Split('\n').Skip(3).TakeWhile(l => l.Length == 19).ToList();
        Assert.NotEmpty(entries);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Substring(0, 10));
            Assert.StartsWith((i + 1) + " 0 obj\n", text.Substring(offset));
        }
        Assert.Equal("Agreement – signed.pdf", SignatureService.SignedFileName("Agreement.docx"));
    }
}

/// <summary>A scripted HTTP handler: answers every request from a function and remembers what was sent.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<(HttpMethod Method, string Url, string? Authorization, string? UserAgent, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), body));
        return _respond(request);
    }
}

public sealed class StaticHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StaticHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, false);
}
