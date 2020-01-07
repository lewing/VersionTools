# MSBuildBumper

Bumps msbuild reference in mono's `packaging/MacSDK/msbuild.py`. This can find existing pull requests and update them, if required, or open new ones.

Prerequisites:

- A mono working directory. This should not be used for anything else, as we will git-reset/git-clean it, whenever required.
- GitHub Personal Access Token (https://help.github.com/en/github/authenticating-to-github/creating-a-personal-access-token-for-the-command-line)

Usage:

- Some *required* arguments:
	- `-t <github_personal_access_token>`
	- `--github-user-name <username>` - Branches will be pushed to this user's mono fork
	- `--remote-name <name>` - This is the remote name in the mono working directory which will be used like `$ git fetch $remote_name`

	- These arguments can be added to a `args.rsp` file and passed to the command line as `@args.rsp`

- Update reference for msbuild branch `mono-2019-10` in mono branch `2019-10`:

`dotnet exec bin/Debug/netcoreapp3.1/MSBuildBumper.dll  --mono-working-dir ~/dev/mono-for-updates/ --msbuild-branch mono-2019-10  --mono-branch 2019-10 @args.rsp`

- Use `-n` to make only local changes and *not* git-push to remote
