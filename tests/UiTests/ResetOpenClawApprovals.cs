using System;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Orbweaver.UiTests.ResetOpenClawApprovals]

namespace Orbweaver.UiTests;

// The same reset as tests/UnitTests/ResetOpenClawApprovals.cs, for this assembly:
// approvals live in process-wide statics, and no test class is trusted to remember
// to clear them. Only around tests in the "Settings" collection, which is serialised:
// separate collections run in parallel, and a reset fired from an unrelated class in
// the middle of another's test would wipe its state.
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class ResetOpenClawApprovalsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => Reset(methodUnderTest);

    public override void After(MethodInfo methodUnderTest, IXunitTest test) => Reset(methodUnderTest);

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
