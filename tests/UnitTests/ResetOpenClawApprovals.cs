using System;
using System.Reflection;
using System.Linq;
using Xunit;
using Xunit.Sdk;

[assembly: Orbweaver.Tests.ResetOpenClawApprovals]

namespace Orbweaver.Tests;

// Forgets the approvals state around every test in this assembly.
//
// OpenClawSessions holds pending approvals, endings, answered ids and
// session-sourced marks in process-wide statics, because that is what the app
// needs. A test that raised an approval then left the next test's session waiting
// on a person it never asked for, which showed up as an unrelated parse test failing
// only in a wide run. Asking each class to remember a cleanup call leaves the next
// author the same trap, so this runs for every test whatever its class does, before
// it and after it.
//
// Only around tests in the "Settings" collection. xunit runs separate collections in
// parallel, so a reset on EVERY test would fire from unrelated classes in the middle
// of another class's test and wipe its state; the Settings collection is serialised,
// and it is where every class that touches OpenClawSessions' statics lives.
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class ResetOpenClawApprovalsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset(methodUnderTest);

    public override void After(MethodInfo methodUnderTest) => Reset(methodUnderTest);

    private static void Reset(MethodInfo method)
    {
        // Read from the attribute's constructor argument: the attribute type does not
        // expose its name in every xunit version this repository has used.
        var inSettings = method.DeclaringType!.GetCustomAttributesData().Any(d =>
            d.AttributeType.Name == "CollectionAttribute"
            && d.ConstructorArguments.Count > 0
            && d.ConstructorArguments[0].Value as string == "Settings");

        if (inSettings) OpenClawSessions.ForgetApprovalsForTests();
    }
}
