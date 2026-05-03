#!/bin/bash

# gsd
# npx get-shit-done-cc --claude --local --install --sdk



cd /mnt/p/dynamic-pool

clear

# claude update

IS_SANDBOX=1 claude \
 --allow-dangerously-skip-permissions \
"$@"

