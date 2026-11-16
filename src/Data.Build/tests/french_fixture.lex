# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
#
# Test fixture for build_grammar.py and build_grammar_sets.py: lines in the French.lex format (form, lemma, tags),
# most of them taken from Grammalecte « Lexique simplifié » v7.7 (lexicons/French.lex, commit 08511c2…) — MPL-2.0.
# Six are ours or edited (« poète », the three « porter » verb lines, the second « événement » line, « Paris »).

# :POS ;LEX ~SEM =FQ /DIC
maison	maison	:N:f:s/*
maisons	maison	:N:f:p/*
petit	petit	:N:A:m:s/*
petite	petit	:N:A:f:s/*
petits	petit	:N:A:m:p/*
gris	gris	:N:A:m:i;C/*
poète	poète	:N:e:s/*
les	les	:G:D:e:p/*
les	les	:G:Oo:Ov:e:p/*
le	le	:G:D:m:s/*
aux	aux	:G:R:D:e:p/*
deux	deux	:B:e:p/*
il	il	:G:Os:O3:m:s/*
ils	ils	:G:Os:O3:m:p/*
et	et	:G:Cc/*
à	à	:G:R:Rv/*
vite	vite	:W/*
mange	manger	:V1_it_x__a:Ip:Sp:1s:3s/*
mange	manger	:V1_it_x__a:E:2s/*
mangent	manger	:V1_it_x__a:Ip:Sp:3p/*
mangez	manger	:V1_it_x__a:Ip:2p/*
mangez	manger	:V1_it_x__a:E:2p/*
mangé	manger	:V1_it_x__a:Q:A:m:s:1ŝ/*
mangée	manger	:V1_it_x__a:Q:A:f:s/*
manger	manger	:V1_it_x__a:Y/*
manger	manger	:N:m:s/*
mangeant	manger	:V1_it_x__a:P/*
mangeront	manger	:V1_it_x__a:If:3p!/*
est	être	:V0ei_____a:Ip:3s/*
est	est	:N:m:s/*
sont	être	:V0ei_____a:Ip:3p!/*
a	avoir	:V0ait____a:Ip:3s/*
a	a	:N:m:i/*
aller	aller	:V1_i__e_e_:Y/*
allée	aller	:V1_i__e_e_:Q:f:s/*
allée	allée	:N:f:s/*
vais	aller	:V1_i__e_e_:Ip:1s/*
porte	porte	:N:f:s/*
portes	porte	:N:f:p/*
porte	porter	:V1_it____a:Ip:Sp:1s:3s/*
portes	porter	:V1_it____a:Ip:Sp:2s/*
portent	porter	:V1_it____a:Ip:Sp:3p/*
semble	sembler	:V1ei_____a:Ip:Sp:1s:3s/*
cœur	cœur	:N:m:s/*
coefficient	coefficient	:N:m:s/*
évènement	évènement	:N:m:s/*
événement	événement	:N:m:s/M
événement	événement	:N:m:s/*
aujourd’hui	aujourd’hui	:W/*
Paris	Paris	:MP:e:i/*
