# GitHub Self-Hosted Runners

This context describes the lifecycle and operation of GitHub Actions runners hosted on Azure virtual machines.

## Language

**Runner**:
A GitHub Actions process that receives and executes one workflow job.
_Avoid_: Runner agent

**Runner VM**:
An Azure virtual machine hosting exactly one runner. It is retired after its job completes.
_Avoid_: Runner (when referring to the virtual machine)

**Runner image**:
A versioned Ubuntu LTS image used to create runner VMs, with a known set of packages and runner software.
_Avoid_: Golden image

**Warm pool**:
A configurable minimum number of idle runner VMs kept ready for upcoming jobs; the minimum may be zero.
_Avoid_: Idle runners (when referring to the configured capacity)

**Runner group**:
An organization-level GitHub Actions group that controls which repositories may assign jobs to a runner pool.
_Avoid_: Runner pool (when referring specifically to GitHub's access-control group)

**Image inventory**:
The exact package and runner-software versions associated with a runner image.
_Avoid_: Package list (when referring to a versioned image's contents)
