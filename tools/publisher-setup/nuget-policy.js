// Runs only inside the authenticated NuGet policy editor. Uses its own CSRF
// handling and same-origin request shape, as implemented by NuGetGallery.
async ({policy, apply}) => {
    if (location.origin !== 'https://www.nuget.org' || typeof initialData !== 'object') throw Error('NuGet account policy editor required');
    const data = initialData;
    if (data.PackagePushScope !== 'package:push' || !Array.isArray(data.PackageOwners) || !Array.isArray(data.Policies)) throw Error('Unsupported policy-editor version; no policy changed');
    const owner = data.PackageOwners.find(p => p.Owner.toLowerCase() === policy.owner.toLowerCase());
    if (!owner) return {policy:policy.name,status:'OWNER_NOT_AVAILABLE',owner:policy.owner,availableOwners:data.PackageOwners.map(p=>p.Owner)};
    if (owner.CanPushNew !== true || owner.CanPushExisting !== true) return {policy:policy.name,status:'OWNER_PERMISSIONS_REVIEW_REQUIRED',owner:owner.Owner,canPushNew:owner.CanPushNew===true,canPushExisting:owner.CanPushExisting===true};
    const expected = {
        Name: 'GitHubActions', RepositoryOwner: policy.repositoryOwner,
        RepositoryOwnerId: String(policy.repositoryOwnerId), Repository: policy.repository,
        RepositoryId: String(policy.repositoryId), WorkflowFile: policy.workflow, Environment: ''
    };
    const sameIds = d => d?.RepositoryOwnerId === expected.RepositoryOwnerId && d?.RepositoryId === expected.RepositoryId;
    // The pinned account view renders scope descriptions, not raw scope IDs.
    // Unknown/localized descriptions require review rather than assuming access.
    const pushOnly = p => Array.isArray(p.PolicyScopes) && p.PolicyScopes.length === 1 &&
        [data.PackagePushScope, 'Push new packages and package versions'].includes(p.PolicyScopes[0]);
    const enabled = p => p.IsOwnerValid === true && p.PolicyDetails?.IsPermanentlyEnabled === true;
    const matches = data.Policies.filter(p => {
        const d = p.PolicyDetails;
        return p.Owner?.toLowerCase() === policy.owner.toLowerCase() && p.PublisherName === 'GitHubActions' &&
            d?.RepositoryOwner?.toLowerCase() === expected.RepositoryOwner.toLowerCase() &&
            d?.Repository?.toLowerCase() === expected.Repository.toLowerCase() &&
            d?.WorkflowFile === expected.WorkflowFile && !(d.Environment || '');
    });
    const packages = [...policy.packages].sort();
    if (matches.length) {
        const exact = matches.find(p => sameIds(p.PolicyDetails) &&
            JSON.stringify([...(p.PolicySubjects || [])].sort()) === JSON.stringify(packages) &&
            pushOnly(p) && enabled(p));
        if (exact) return {policy: policy.name, status: 'ALREADY_CONFIGURED', policyId: exact.Key, packages};
        // The server renders existing scopes as localized descriptions. Preserve
        // existing policies and report their exact scope for account-owner review;
        // never silently broaden, replace, or delete them.
        return {policy: policy.name, status: 'EXISTING_SCOPE_REVIEW_REQUIRED', existing: matches.map(p => ({name:p.PolicyName,packages:p.PolicySubjects || ['*'],scopes:p.PolicyScopes || ['All']}))};
    }
    if (data.Policies.some(p => p.PolicyName === policy.name)) throw Error('Policy name already has different repository/workflow criteria');
    if (!apply) return {policy:policy.name,status:'WOULD_CREATE',criteria:expected,packages};
    const url = new URL(data.GenerateUrl, location.href);
    if (url.origin !== location.origin) throw Error('Cross-origin policy endpoint refused');
    const payload = {policyName:policy.name,owner:policy.owner,criteria:JSON.stringify(expected),publisherType:'GitHubActions',policyScopes:[data.PackagePushScope],policySubjects:packages};
    window.nuget.addAjaxAntiForgeryToken(payload);
    const created = await new Promise((resolve,reject) => window.jQuery.ajax({url:url.href,type:'POST',dataType:'json',data:payload,
        success:resolve,error:x=>reject(Error('NuGet policy create failed: HTTP '+x.status))}));
    const d = created.PolicyDetails;
    if (!pushOnly(created) || !enabled(created) || created.Owner?.toLowerCase() !== policy.owner.toLowerCase() || !sameIds(d) || d?.RepositoryOwner?.toLowerCase() !== expected.RepositoryOwner.toLowerCase() || d?.Repository?.toLowerCase() !== expected.Repository.toLowerCase() || (d.Environment || '') || created.PublisherName !== 'GitHubActions' || d?.WorkflowFile !== policy.workflow || JSON.stringify([...(created.PolicySubjects || [])].sort()) !== JSON.stringify(packages)) return {policy:policy.name,status:'CREATION_VERIFICATION_REQUIRED',policyId:created.Key,packages};
    return {policy:policy.name,status:'CREATED',policyId:created.Key,packages};
}
