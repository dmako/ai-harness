git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
| repository | libgit2 opens | HEAD | file list (ls-files) | dirty paths (status) | old file (show HEAD~1:src/a.cs) |
|---|---|---|---|---|---|
| plain | yes | same | same (4) | same (1) | same |
| sha256 objects | **no**: LibGit2SharpException: unknown object format 'sha256' | | | | |
| reftable refs | **no**: LibGit2SharpException: unsupported extension name extensions.refstorage | | | | |
| index v4 | yes | same | same (4) | same (1) | same |
| split index | yes | same | **fails**: LibGit2SharpException: unsupported mandatory extension: 'link' | **fails**: LibGit2SharpException: unsupported mandatory extension: 'link' | same |
| untracked cache | yes | same | same (4) | same (1) | same |
| sparse checkout (cone) | yes | same | same (4) | **differs**: git [untracked.txt] libgit2 [doc/b.md, untracked.txt] | same |
| sparse index | yes | same | **fails**: LibGit2SharpException: unsupported mandatory extension: 'sdir' | **fails**: LibGit2SharpException: unsupported mandatory extension: 'sdir' | same |
| worktreeConfig extension | yes | same | same (4) | same (1) | same |
| linked worktree | yes | same | same (3) | same (0) | same |
| submodule | yes | same | same (6) | same (1) | same |
| shared clone (alternates) | yes | same | same (3) | same (0) | same |
| shallow clone | yes | same | same (3) | same (0) | no HEAD~1 in either (shallow) |
| partial clone (blob:none) | yes | same | same (3) | same (0) | **fails**: NotFoundException: No valid git object identified by 'a869c28495266ce277963307009946887ad25470' exists in the repository. |
| core.autocrlf=true, CRLF work tree | yes | same | same (4) | same (2) | same |
| refs packed (gc) | yes | same | same (4) | same (1) | same |
| commit-graph + multi-pack-index | yes | same | same (4) | same (1) | same |
