# Use ephemeral Azure VM Scale Set runners from versioned images

The runner service will host one GitHub Actions runner process on each Azure VM, assign each VM one job, then retire it. It will create those VMs from versioned Ubuntu LTS images with pinned package inventories and autoscale them from queued demand. This favors isolated, reproducible build environments and exact package-version visibility, while preserving a path to Windows runners, over the lower startup cost and simpler lifecycle of persistent VMs or the different operating model of Kubernetes-hosted runner scale sets.
