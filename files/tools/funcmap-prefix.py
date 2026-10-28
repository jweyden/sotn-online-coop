#!/usr/bin/env python3
"""Prefix the characters' function names that clash with the game's.

usage: funcmap-prefix.py FUNCMAPS_DIR

The recompiler names each function after its symbol; PSP Maria, Saturn Maria and the
long-coat Richter are built from the same decomp code as Richter and the game, so many
names repeat (the recompiler would suffix every copy). The co-op mod's hooks and calls
use these names, so they must come out the same on every machine:
  maria.json     mar_  for names the base game's maps have
  mariasat.json  mas_  for names any other map has (PSP Maria's included)
  richter2.json  r2_   likewise
"""
import glob, json, os, sys

d = sys.argv[1]
CHARACTERS = (("maria.json", "mar_"), ("mariasat.json", "mas_"), ("richter2.json", "r2_"))
mine = {name for name, _ in CHARACTERS}


def names(files):
    out = set()
    for f in files:
        out |= {x.get("name") for x in json.load(open(f))["functions"]}
    return out


for name, prefix in CHARACTERS:
    path = os.path.join(d, name)
    if not os.path.isfile(path):
        continue
    others = [f for f in glob.glob(os.path.join(d, "*.json")) if os.path.basename(f) != name]
    if name == "maria.json":
        others = [f for f in others if os.path.basename(f) not in mine]
    taken = names(others)
    m = json.load(open(path))
    for x in m["functions"]:
        if x["name"] in taken and not x["name"].startswith(prefix):
            x["name"] = prefix + x["name"]
    json.dump(m, open(path, "w"), indent=2)
