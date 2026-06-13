# GitHub Information

Reference notes for the Git/GitHub setup of this project.

## Repository

- **URL:** https://github.com/keitherickson/ClaudeCore
- **Visibility:** Public ⚠️ (anyone on the internet can view the code)
- **Default branch:** `main`
- **Remote:** `origin` → `https://github.com/keitherickson/ClaudeCore.git`

## Commit identity

- **Name:** Keith Erickson
- **Email:** keitherickson@hotmail.com

(Set globally via `git config --global user.name` / `user.email`.)

## Authentication

- Authenticated through the **GitHub CLI (`gh`)**, version 2.94.0
- GitHub account: **keitherickson**
- Git protocol: HTTPS (token stored in the Windows keyring)
- `gh` install location: `C:\Program Files\GitHub CLI\gh.exe`
  - A new terminal window will have `gh` on the PATH automatically.

## Saving future work (the everyday loop)

```powershell
git add -A
git commit -m "Describe what you changed"
git push
```

## Useful commands

```powershell
git status            # See what has changed
git log --oneline     # View commit history
git pull              # Get the latest from GitHub
gh repo view --web    # Open the repo in the browser
```

## Notes

- `.gitignore` already excludes build output (`bin/`, `obj/`), secrets, and user-specific files.
- Because the repo is **public**, never commit API keys, passwords, or connection
  strings into tracked files such as `appsettings.json`.
- To make the repo private later: `gh repo edit --visibility private`
  (requires confirming the change).

---
_Initial setup committed as `87d67e4` — ASP.NET Core MVC project scaffold._
