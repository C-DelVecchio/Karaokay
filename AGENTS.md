# AGENTS.md

## Shell Rules

SHELL RULE: Never chain commands with &&, ||, ;, or |. Execute exactly ONE standalone command per shell call (use --prefix or --project flags instead of cd ... &&). If 'Permission denied: shell' occurs, retry as a single unchained command matching your allowed prefix rather than assuming the tool is disabled.
