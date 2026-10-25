namespace Harness.Infrastructure.Completion;

public static class CompletionScripts
{
    public static string For(string shell) => Script(shell).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Script(string shell) =>
        shell switch
        {
            "zsh" => """
                #compdef harness
                _harness() {
                  local -a items
                  items=("${(@f)$(harness __complete zsh "${(@)words[2,CURRENT]}" 2>/dev/null)}")
                  _describe 'harness' items
                }
                if (( $+functions[compdef] )); then compdef _harness harness; else autoload -Uz compinit && compinit && compdef _harness harness; fi
                """ + "\n",
            "bash" => """
                _harness() {
                  local -a words=("${COMP_WORDS[@]:1:COMP_CWORD}")
                  local cands
                  cands=$(harness __complete bash "${words[@]}" 2>/dev/null)
                  local IFS=$'\n'
                  COMPREPLY=($(compgen -W "$cands" -- "${COMP_WORDS[COMP_CWORD]}"))
                }
                complete -o default -F _harness harness
                """ + "\n",
            "fish" => """
                complete -c harness -f -a '(harness __complete fish (commandline -opc)[2..-1] (commandline -ct) 2>/dev/null)'
                """ + "\n",
            "pwsh" => """
                Register-ArgumentCompleter -Native -CommandName harness, harness.exe -ScriptBlock {
                  param($wordToComplete, $commandAst, $cursorPosition)
                  $words = @($commandAst.CommandElements | Select-Object -Skip 1 |
                    Where-Object { $_.Extent.EndOffset -le $cursorPosition } | ForEach-Object { $_.ToString() })
                  if ($wordToComplete -ne '' -and $words.Count -gt 0) { $words = @($words | Select-Object -SkipLast 1) }
                  harness __complete pwsh ("=" + $wordToComplete) @words 2>$null | ForEach-Object {
                    $name, $desc = $_ -split "`t", 2
                    if ($name -like "$wordToComplete*") {
                      [System.Management.Automation.CompletionResult]::new($name, $name, 'ParameterValue', $(if ($desc) { $desc } else { $name }))
                    }
                  }
                }
                """ + "\n",
            _ => throw new ArgumentException("unknown shell (want zsh, bash, fish or pwsh)", nameof(shell)),
        };
}
