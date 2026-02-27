# Git Rules for WSL/Windows Sharing without Chaos


Here are the “stop the End of Line spam forever” settings for **Windows + Windows Subsystem for Linux** sharing the same repos.

## The one rule that prevents 90 percent of pain

**Work on repos from inside the Linux filesystem** (for example, `\\wsl$\Ubuntu\home\you\code\repo`), not `/mnt/c/...`.

If you must keep the repo on the Windows drive, you can still make this work, but line endings plus file permission metadata are more likely to churn.

---

## Best-practice setup (recommended)

### 1) Make Git treat the repo as Linux-first: use `.gitattributes`

Add this to the repo root:

```gitattributes
# Default: commit LF, normalize on commit
* text=auto eol=lf

# Keep Windows scripts as CRLF if you have them
*.bat  text eol=crlf
*.cmd  text eol=crlf
*.ps1  text eol=crlf

# Shell scripts should be LF
*.sh   text eol=lf

# Don’t touch binaries
*.png  -text
*.jpg  -text
*.zip  -text
*.pdf  -text
```

This makes **the repository itself** enforce consistent endings, regardless of whether the commit came from Windows or Linux.

### 2) Set global Git config on each side (Windows Git and WSL Git)

You want: **commit LF, do not auto-convert back and forth behind your back**.

#### In Windows (PowerShell or Git Bash):

```bash
git config --global core.autocrlf false
git config --global core.eol lf
git config --global core.safecrlf warn
```

#### In WSL (Ubuntu shell):

```bash
git config --global core.autocrlf false
git config --global core.eol lf
git config --global core.safecrlf warn
```

Why this combo works:

* `core.autocrlf false` prevents “Git helpfully rewrote your files” churn.
* `.gitattributes` becomes the single source of truth for what gets committed.

---

## If your repo is already “infected” (huge diffs already happening)

After adding `.gitattributes`, run a one-time normalization:

```bash
git add --renormalize .
git status
git commit -m "Normalize line endings"
```

That commit should be the last time you ever see “every line changed” from End of Line differences.

If you want to normalize your working tree immediately (especially after changing Git settings), you can also do:

```bash
git rm --cached -r .
git reset --hard
```

(That forces Git to re-checkout files using your current rules.)

---

## Two extra settings that commonly cause “spam”

### 1) WSL on `/mnt/c` permission noise

If you keep repos under `/mnt/c/...`, Git can churn on mode/permission bits.

Inside the repo, set:

```bash
git config core.filemode false
```

### 2) VS Code settings

If you use Visual Studio Code on Windows or inside WSL, set:

* **Files: Eol** = `\n`
* **Editor: Render Whitespace** optional, but it makes it obvious when something is wrong.

---

## Quick “sanity check” commands

See what Git thinks your settings are:

```bash
git config --show-origin --get core.autocrlf
git config --show-origin --get core.eol
git config --show-origin --get core.safecrlf
```

Check whether a file is being treated as text and normalized (after `.gitattributes` is present):

```bash
git check-attr -a -- path/to/file
```

---

If you tell me where your repo lives (**WSL home** vs **/mnt/c**) and whether you have any Windows-only scripts (`.bat`, `.ps1`), I can tailor the exact `.gitattributes` to your layout.
