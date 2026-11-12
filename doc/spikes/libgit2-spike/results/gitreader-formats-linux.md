git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
| repository | library | opens | HEAD | file list (ls-files) | dirty paths (status) | old file (HEAD~1:src/a.cs) |
|---|---|---|---|---|---|---|
| plain | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | same (4) | same (1) | same |
| sha256 objects | libgit2 | **no**: LibGit2SharpException: unknown object format 'sha256' | | | | |
| | GitReader | yes | **fails**: ArgumentException: Invalid hash string. | **fails**: InvalidDataException: Broken Git index file: File=<scratch>/sha256/.git/index, Step=6, Entry=0/3, PathLength=2440, Expected=2441, Actual=359, Position=433, Length=433 | **fails**: InvalidDataException: Broken Git index file: File=<scratch>/sha256/.git/index, Step=6, Entry=0/3, PathLength=2440, Expected=2441, Actual=359, Position=433, Length=433 | **fails**: ArgumentException: Invalid hash string. |
| reftable refs | libgit2 | **no**: LibGit2SharpException: unsupported extension name extensions.refstorage | | | | |
| | GitReader | yes | **fails**: InvalidOperationException: Nullable object must have a value. | same (4) | **differs**: git [untracked.txt] library [.gitignore, doc/b.md, src/a.cs, untracked.txt] | **fails**: InvalidOperationException: Nullable object must have a value. |
| index v4 | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/index4/.git/index, Entry=0/3, Expected=0x00, Actual=0x65 | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/index4/.git/index, Entry=0/3, Expected=0x00, Actual=0x65 | same |
| split index | libgit2 | yes | same | **fails**: LibGit2SharpException: unsupported mandatory extension: 'link' | **fails**: LibGit2SharpException: unsupported mandatory extension: 'link' | same |
| | GitReader | yes | same | **fails**: ArgumentException: An item with the same key has already been added. Key:  | **fails**: ArgumentException: An item with the same key has already been added. Key:  | same |
| untracked cache | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | same (4) | same (1) | same |
| sparse checkout (cone) | libgit2 | yes | same | same (4) | **differs**: git [untracked.txt] library [doc/b.md, untracked.txt] | same |
| | GitReader | yes | same | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/sparse/.git/index, Entry=1/3, Expected=0x00, Actual=0x6D | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/sparse/.git/index, Entry=1/3, Expected=0x00, Actual=0x6D | same |
| sparse index | libgit2 | yes | same | **fails**: LibGit2SharpException: unsupported mandatory extension: 'sdir' | **fails**: LibGit2SharpException: unsupported mandatory extension: 'sdir' | same |
| | GitReader | yes | same | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/sparseindex/.git/index, Entry=1/3, Expected=0x00, Actual=0x63 | **fails**: InvalidDataException: Missing null terminator: File=<scratch>/sparseindex/.git/index, Entry=1/3, Expected=0x00, Actual=0x63 | same |
| worktreeConfig extension | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | same (4) | same (1) | same |
| linked worktree | libgit2 | yes | same | same (3) | same (0) | same |
| | GitReader | yes | same | **differs**: git [.gitignore, doc/b.md, src/a.cs] library [.gitignore, doc/b.md, src/a.cs, untracked.txt] | **differs**: git [] library [untracked.txt] | same |
| submodule | libgit2 | yes | same | same (6) | same (1) | same |
| | GitReader | yes | same | same (6) | same (1) | same |
| shared clone (alternates) | libgit2 | yes | same | same (3) | same (0) | same |
| | GitReader | yes | same | same (3) | **differs**: git [] library [.gitignore, doc/b.md, src/a.cs] | no HEAD~1 (shallow) |
| shallow clone | libgit2 | yes | same | same (3) | same (0) | no HEAD~1 (shallow) |
| | GitReader | yes | same | same (3) | same (0) | **fails**: InvalidOperationException: Nullable object must have a value. |
| partial clone (blob:none) | libgit2 | yes | same | same (3) | same (0) | **fails**: NotFoundException: No valid git object identified by 'a869c28495266ce277963307009946887ad25470' exists in the repository. |
| | GitReader | yes | same | same (3) | same (0) | **fails**: InvalidDataException: Couldn't find tree object: a869c28495266ce277963307009946887ad25470 |
| core.autocrlf=true, CRLF work tree | libgit2 | yes | same | same (4) | same (2) | same |
| | GitReader | yes | same | same (4) | same (2) | same |
| refs packed (gc) | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | same (4) | same (1) | same |
| commit-graph + multi-pack-index | libgit2 | yes | same | same (4) | same (1) | same |
| | GitReader | yes | same | same (4) | same (1) | same |
