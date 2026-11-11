const fs=require('node:fs');const vm=require('node:vm');const assert=require('node:assert/strict');
const script=fs.readFileSync(__dirname+'/nuget-policy.js','utf8');
const policy={name:'driver-preview',owner:'owner',repositoryOwner:'owner',repositoryOwnerId:7,repository:'driver',repositoryId:11,workflow:'driver-preview.yml',packages:['Device.Package']};
async function run({apply=false,policies=[],owners=[{Owner:'owner',CanPushNew:true,CanPushExisting:true}],origin='https://www.nuget.org',url='/account/generate',mutate=()=>{}}={}){
    const posts=[];const data={PackagePushScope:'package:push',PackageOwners:owners,Policies:policies,GenerateUrl:url};
    const context={URL,location:{origin,href:origin+'/account/TrustedPublishing'},initialData:data,window:{nuget:{addAjaxAntiForgeryToken:p=>p.csrf='fixture-only'},jQuery:{ajax:options=>{
        posts.push(options.data);const criteria=JSON.parse(options.data.criteria);
        const created={Owner:options.data.owner,PublisherName:'GitHubActions',Key:123,IsOwnerValid:true,PolicyDetails:{...criteria,RepositoryOwnerId:String(criteria.RepositoryOwnerId),RepositoryId:String(criteria.RepositoryId),IsPermanentlyEnabled:true,EnabledDaysLeft:2147483647},PolicySubjects:options.data.policySubjects,PolicyScopes:['Push new packages and package versions']};mutate(created);options.success(created);
    }}}};
    const result=await vm.runInNewContext('('+script+')',context)({policy,apply});return {result,posts};
}
(async()=>{
    let test=await run();assert.equal(test.result.status,'WOULD_CREATE');assert.equal(test.posts.length,0);
    test=await run({apply:true,owners:[{Owner:'different-owner',CanPushNew:true,CanPushExisting:true}]});assert.equal(test.result.status,'OWNER_NOT_AVAILABLE');assert.equal(JSON.stringify(test.result.availableOwners),'["different-owner"]');assert.equal(test.posts.length,0);
    for (const permissions of [{CanPushNew:false,CanPushExisting:true},{CanPushNew:true,CanPushExisting:false},{CanPushNew:'true',CanPushExisting:true}]) {
        test=await run({apply:true,owners:[{Owner:'owner',...permissions}]});assert.equal(test.result.status,'OWNER_PERMISSIONS_REVIEW_REQUIRED');assert.equal(test.posts.length,0);
    }
    test=await run({apply:true});assert.equal(test.result.status,'CREATED');assert.equal(test.posts.length,1);
    assert.equal(JSON.stringify(test.posts[0].policyScopes),'["package:push"]');assert.equal(JSON.stringify(test.posts[0].policySubjects),'["Device.Package"]');assert.equal(test.posts[0].csrf,'fixture-only');
    const existing={Owner:'owner',PublisherName:'GitHubActions',PolicyName:'driver-preview',Key:321,IsOwnerValid:true,PolicyDetails:{RepositoryOwner:'owner',RepositoryOwnerId:'7',Repository:'driver',RepositoryId:'11',IsPermanentlyEnabled:true,EnabledDaysLeft:2147483647,WorkflowFile:'driver-preview.yml',Environment:''},PolicyScopes:['Push new packages and package versions'],PolicySubjects:['Device.Package']};
    test=await run({apply:true,policies:[existing]});assert.equal(test.result.status,'ALREADY_CONFIGURED');assert.equal(test.posts.length,0);
    for(const change of [{PolicyScopes:['all']},{PolicySubjects:['*']},{PolicyDetails:{...existing.PolicyDetails,RepositoryId:12}},{IsOwnerValid:false},{PolicyDetails:{...existing.PolicyDetails,IsPermanentlyEnabled:false,EnabledDaysLeft:0}}]){
        test=await run({apply:true,policies:[{...existing,...change}]});assert.equal(test.result.status,'EXISTING_SCOPE_REVIEW_REQUIRED');assert.equal(test.posts.length,0);
    }
    await assert.rejects(run({origin:'https://example.com'}),/editor required/);
    await assert.rejects(run({apply:true,url:'https://example.com/write'}),/Cross-origin/);
    assert.equal((await run({apply:true,mutate:created=>created.PolicyDetails.RepositoryOwnerId=8})).result.status,'CREATION_VERIFICATION_REQUIRED');
    assert.equal((await run({apply:true,mutate:created=>created.PolicyScopes=[]})).result.status,'CREATION_VERIFICATION_REQUIRED');
    assert.equal((await run({apply:true,mutate:created=>created.IsOwnerValid=false})).result.status,'CREATION_VERIFICATION_REQUIRED');
    assert.equal((await run({apply:true,mutate:created=>created.PolicyDetails.IsPermanentlyEnabled=false})).result.status,'CREATION_VERIFICATION_REQUIRED');
    await assert.rejects(run({apply:true,policies:[{PolicyName:'driver-preview'}]}),/different/);
    console.log('PASS NuGet account-page policy checks: exact scope, idempotence, conflicting/disabled policies, origin and created tuple');
})().catch(error=>{console.error(error);process.exitCode=1;});
