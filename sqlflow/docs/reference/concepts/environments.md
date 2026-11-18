---
id: concept-environments
title: "Environments: the estate's separate deployments, named and opened from the title bar"
type: concept
summary: How a deployment names its environment and lists the estate's others (ControlPlane:Environments), and the title bar menu that opens them.
keywords:
  - environments
  - environment switcher
  - switch environment
  - dev test prod
  - several environments
  - separate deployments
  - instance
  - ControlPlane:Environments
  - ControlPlane:Environments:Current
  - ControlPlane:Environments:Links
  - ControlPlane__Environments__Links__0__Url
  - GET /environments
  - title bar
related:
  - concept-control-plane
  - concept-repository-auto-sync
  - guide-deployment
sourceRefs:
  - src/SqlFlow.ControlPlane/Configuration/EnvironmentsOptions.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Api/EnvironmentEndpoints.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - gui/src/layout/workbench/EnvironmentSwitcher.tsx
  - gui/src/layout/workbench/environmentMenu.ts
  - gui/src/layout/workbench/TitleBar.tsx
---

# Environments

An estate that runs dev, test and prod runs each as a deployment of its own: its own control plane, catalog, nodes and
sign-in, each moving to a new commit of the repository only when that environment syncs it
([Automatic sync of a repository](repository-auto-sync.md)). The environments setting tells each deployment which one it
is and where the others are, so the GUI's title bar names the environment a person is working in and opens another one's
GUI from a menu, rather than from bookmarks. The menu only links: each environment keeps its own data and signs a person
in itself.

## The setting

`ControlPlane:Environments`, in the control plane's configuration. `Current` names the environment this control plane is;
`Links` lists every environment of the estate, this one among them, in the order the menu shows them. Every deployment
can be given the same list, so they differ only in `Current`.

| Key | Meaning |
| --- | --- |
| `Environments:Current` | The name of the environment this control plane is. Required when `Links` lists any, and then one of their names (case does not matter; the list's spelling is shown). Set alone, the title bar names the environment and offers no other. |
| `Environments:Links:<n>:Name` | An environment's name, as the title bar shows it: `dev`, `test`, `prod`. |
| `Environments:Links:<n>:Url` | The address of that environment's GUI, which the menu opens. |
| `Environments:Links:<n>:Description` | What the environment is for, in one line, shown under its name. Optional. |

Nothing is set by default, and the title bar then shows no environment.

As environment variables, the form deployments set (`__` for each `:`), the test deployment of an estate of three:

```text
ControlPlane__Environments__Current=test
ControlPlane__Environments__Links__0__Name=dev
ControlPlane__Environments__Links__0__Url=https://sqlflow-dev.example.com
ControlPlane__Environments__Links__0__Description=Development
ControlPlane__Environments__Links__1__Name=test
ControlPlane__Environments__Links__1__Url=https://sqlflow-test.example.com
ControlPlane__Environments__Links__1__Description=Acceptance testing
ControlPlane__Environments__Links__2__Name=prod
ControlPlane__Environments__Links__2__Url=https://sqlflow.example.com
ControlPlane__Environments__Links__2__Description=Production
```

The dev and prod deployments carry the same lines with `Current` set to `dev` and to `prod`.

## What start refuses

The control plane checks the setting with the rest of its configuration and refuses to start on the first value it cannot
show, naming the setting:

| Rule | Detail |
| --- | --- |
| Names | Letters, digits, `-`, `_` and `.`, starting with a letter or a digit, at most 64 characters; each environment listed once, ignoring case. |
| Current | Set whenever `Links` lists any, and one of their names. |
| Addresses | An absolute `http` or `https` address, at most 2,000 characters, with no user name or password, query or fragment. A refusal never repeats the address, since a mistyped one could carry a credential. |
| Descriptions | At most 400 characters, on one line. |
| Count | At most 20 environments. |

For example:
`ControlPlane:Environments:Current is 'stage', which ControlPlane:Environments:Links does not list (dev, test, prod): name one of them, or add this environment to the list with its address.`
and
`ControlPlane:Environments:Links:2:Url carries a user name or a password: give the address of the environment's GUI alone, since each environment signs a person in itself.`

## The API

`GET /api/v1/environments` (policy `read`) answers the setting as the title bar shows it: `current`, the name of the
environment this control plane is (null when none is configured), and `environments`, each with `name`, `description`,
`url` and `current`, in the configured order (empty when only the current one is named). It answers a signed-in caller
only, since the list names the estate's other addresses, and is closed to an assistant run's delegated token.

```json
{
  "current": "test",
  "environments": [
    { "name": "dev", "description": "Development", "url": "https://sqlflow-dev.example.com", "current": false },
    { "name": "test", "description": "Acceptance testing", "url": "https://sqlflow-test.example.com", "current": true },
    { "name": "prod", "description": "Production", "url": "https://sqlflow.example.com", "current": false }
  ]
}
```

## In the GUI

The title bar shows **Environment** and the current environment's name, just before the controls modules add. With
other environments listed, it opens a menu of every environment in order: the current one checked, each other one a link
to its GUI with its description and address. Following one opens that environment's start page, in the same tab (or a new
one, as any link can), where it signs the person in. With no other environment it names this one without a menu, and with
none configured it shows nothing. The GUI links only an `http` or `https` address, whatever the list holds.

## See also

- [The control plane](control-plane.md#configuration): the `ControlPlane` settings beside this one.
- [Automatic sync of a repository](repository-auto-sync.md): an environment that moves to a new commit only when it is
  synced.
- [Deploying](../guides/deployment.md): giving a deployment its configuration.
