# SPDX-License-Identifier: GPL-3.0-or-later
# Pépite — invented sentences (« phrases inventées pour Pépite »). From src/: python -I -X utf8 Predict.Eval/grammar/gen_invented.py <out>;
# the committed set is its output byte for byte (plan 11, 10D-0).
"""Writes src/Predict.Eval/grammar/g-invented.jsonl from the sentences below (our own invented French, school
level CE2–6e). Markup: [typed|gold|cls] where typed is what the pupil wrote and gold the correct form."""
import json
import re
import sys

OUT = sys.argv[1]
CLS = {"dn": "det-num", "ag": "adj-gender", "pa": "post-adj", "sv": "subj-verb", "ns": "noun-subj", "ep": "etre-part",
       "ap": "avoir-ppas", "pi": "prep-inf", "mi": "modal-inf", "fa": "fam-a", "fe": "fam-et", "fs": "fam-son",
       "fo": "fam-on", "fc": "fam-ces"}

S = r"""
Les [chat|chats|dn] du voisin dorment au soleil.
J'ai trouvé des [coquillage|coquillages|dn] sur la plage.
Mes [copine|copines|dn] viennent jouer chez moi.
Nous avons planté des [tomate|tomates|dn] dans le jardin.
Léa range ses [crayon|crayons|dn] dans sa trousse.
Les [enfant|enfants|dn] courent dans la cour de l'école.
Ces [fleur|fleurs|dn] sentent très bon.
Le maître corrige nos [cahier|cahiers|dn] le soir.
Il y a des [nuage|nuages|dn] gris dans le ciel.
Tom a mis ses [botte|bottes|dn] pour sortir sous la pluie.
Les [élève|élèves|dn] de la classe préparent un spectacle.
J'ai lu tous les [livre|livres|dn] de la bibliothèque.
Les [baleine|baleines|dn] chantent sous l'eau.
On a vu des [papillon|papillons|dn] dans le pré.
Mes [grands-parent|grands-parents|dn] habitent à la campagne.
Les [pirate|pirates|dn] cherchent un trésor.
Il range les [assiette|assiettes|dn] dans le placard.
Les [vache|vaches|dn] mangent de l'herbe dans le champ.
Zoé a deux [chaton|chatons|dn] tout noirs.
Nous [compton|comptons|sv] les [étoile|étoiles|dn] dans le ciel.
Les [dinosaure|dinosaures|dn] ont disparu il y a très longtemps.
Le facteur [apportent|apporte|ns] les [lettre|lettres|dn] le matin.
Ma mamie a une [petit|petite|ag] maison près de la mer.
C'est une [grand|grande|ag] fête pour toute la classe.
Inès a une [joli|jolie|ag] voix.
Nous avons une [nouveau|nouvelle|ag] maîtresse cette année.
Noah est arrivé à la [dernier|dernière|ag] place.
C'est la [premier|première|ag] fois que je prends l'avion.
Emma a une [vrai|vraie|ag] cabane dans les arbres.
Le loup a une [gros|grosse|ag] voix.
Il raconte une [long|longue|ag] histoire [drôle|drôle|pa] [a|à|fa] sa sœur.
Le château [à|a|fa] une [haut|haute|ag] tour.
On a mangé une [bon|bonne|ag] tarte aux pommes.
J'ai fait une [mauvais|mauvaise|ag] chute à vélo.
Elle porte une [lourd|lourde|ag] valise.
La sorcière habite dans une [vieux|vieille|ag] maison.
Ma tante a une [beau|belle|ag] voiture.
Nathan est mon [meilleure|meilleur|ag] ami.
Il visite une [ancien|ancienne|ag] église.
C'est une [chaud|chaude|ag] journée d'été.
Nous habitons dans une [petit|petite|ag] ville.
Elle a mis sa [nouveau|nouvelle|ag] robe.
Le chat fait une [court|courte|ag] sieste.
Une [grand|grande|ag] vague arrive sur la plage.
J'ai des chaussettes [rouge|rouges|pa] et blanches.
Ma sœur a les cheveux [long|longs|pa] et frisés.
Elle porte une jupe [bleu|bleue|pa] pour la fête [est|et|fe] elle danse.
Les maisons [blanc|blanches|pa] [brille|brillent|ns] au soleil.
J'ai deux amies très [gentil|gentilles|pa].
Le chien a les oreilles [noir|noires|pa].
Nous buvons une soupe [chaud|chaude|pa] le soir.
Les pommes [vert|vertes|pa] sont acides.
Il [à|a|fa] les yeux [bleu|bleus|pa] comme son père.
Les feuilles [mort|mortes|pa] tombent en automne.
On trouve des roches [noir|noires|pa] près du volcan.
La mer est [calme|calme|pa] ce matin.
Les tigres sont des animaux [sauvage|sauvages|pa].
Elle a une voix [doux|douce|pa].
Les nuits [froid|froides|pa] arrivent en hiver.
Les plantes [vert|vertes|pa] ont besoin de lumière.
Le roi vit dans une salle [immense|immense|pa].
Les rues [étroit|étroites|pa] du village sont jolies.
Nous avons des voisins très [poli|polis|pa].
Chloé porte une veste [gris|grise|pa].
Les étoiles [lointain|lointaines|pa] brillent la nuit.
Il a acheté des bottes [neuf|neuves|pa].
Ils [mange|mangent|sv] à la cantine à midi.
Elles [joue|jouent|sv] à la corde à sauter.
Tu [mange|manges|sv] trop de bonbons.
Nous [chante|chantons|sv] pour la fête de l'école.
Vous [chanter|chantez|sv] très bien.
Je [veut|veux|sv] un chocolat chaud.
Ils [fait|font|sv] leurs devoirs après le goûter.
On [jouent|joue|sv] au ballon dans la cour.
Elles [danse|dansent|sv] au spectacle de fin d'année.
Ils [écrit|écrivent|sv] une lettre à leur grand-mère.
Nous [lit|lisons|sv] une histoire chaque soir.
Ils [finit|finissent|sv] leur dessin.
Tu [écrit|écris|sv] très bien.
Je [finit|finis|sv] mon exercice de mathématiques.
Ils [peut|peuvent|sv] venir à la piscine avec nous.
Elles [vit|vivent|sv] dans une grande maison.
Ils [regarde|regardent|sv] un dessin animé.
Je [suit|suis|sv] le chemin jusqu'à la forêt.
Tu [peut|peux|sv] m'aider à porter ce sac ?
Elles [prend|prennent|sv] le bus tous les matins.
Ils [court|courent|sv] très vite.
Nous [mange|mangeons|sv] des crêpes.
Les enfants [mange|mangent|ns] leur goûter dans le parc.
Mes parents [travaille|travaillent|ns] tous les jours.
Les chats [dort|dorment|ns] sur le canapé.
Le chien [aboient|aboie|ns] quand quelqu'un sonne.
Mes copains [arrive|arrivent|ns] à la fête.
Les oiseaux [chante|chantent|ns] le matin.
Le bébé [pleurent|pleure|ns] dans son lit.
Les voisins [part|partent|ns] en vacances demain.
Les abeilles [fabrique|fabriquent|ns] du miel.
Les volcans [crache|crachent|ns] de la lave.
La plante [poussent|pousse|ns] vers la lumière.
Les élèves [écoute|écoutent|ns] la maîtresse.
Les feuilles [tombe|tombent|ns] des arbres en automne.
Les fleuves [coule|coulent|ns] vers la mer.
Les loups [chasse|chassent|ns] la nuit.
Les planètes [tourne|tournent|ns] autour du soleil.
Les poules [pond|pondent|ns] des œufs.
Les lapins [mange|mangent|ns] des carottes.
Mon frère et ma sœur [joue|jouent|ns] ensemble.
Les dauphins [nage|nagent|ns] très vite.
Les fourmis [transporte|transportent|ns] de la nourriture.
Le soleil [brillent|brille|ns] dans le ciel.
Elle est [allé|allée|ep] chez sa grand-mère.
Ils sont [parti|partis|ep] tôt ce matin.
Elles sont [content|contentes|ep] de leur dessin.
Ma mère est [fatigué|fatiguée|ep] ce soir.
Nous sommes [arrivé|arrivés|ep] en retard.
Chloé est [tombé|tombée|ep] dans la cour.
Ils sont [venu|venus|ep] avec leurs parents.
La porte est [fermé|fermée|ep] à clé.
Mes cousines sont [parti|parties|ep] en vacances.
La ville est [détruit|détruite|ep] par la guerre.
Les fenêtres sont [ouvert|ouvertes|ep].
La princesse est [né|née|ep] dans un château.
Les pyramides sont [construit|construites|ep] en pierre.
La reine est [mort|morte|ep] dans le conte.
Les soldats sont [rentré|rentrés|ep] chez eux.
La lettre est [écrit|écrite|ep] à l'encre bleue.
Les graines sont [planté|plantées|ep] au printemps.
Elle est [resté|restée|ep] à la maison.
Les enfants sont [sorti|sortis|ep] dans la cour.
Ma sœur est [monté|montée|ep] dans sa chambre.
La neige est [tombé|tombée|ep] toute la nuit.
Elles sont [descendu|descendues|ep] du bus.
Il a [manger|mangé|ap] une pomme.
J'ai [jouer|joué|ap] avec mon chien.
Elle a [regarder|regardé|ap] un film.
Nous avons [gagner|gagné|ap] le match.
Tu as [finir|fini|ap] tes devoirs ?
Ils ont [trouver|trouvé|ap] un trésor.
On a [danser|dansé|ap] toute la soirée.
J'ai [oublier|oublié|ap] mon cartable.
Vous avez [parler|parlé|ap] à la directrice.
Le volcan a [détruire|détruit|ap] le village.
Le navigateur a [traverser|traversé|ap] l'océan.
Les élèves ont [observer|observé|ap] les insectes.
La maîtresse a [expliquer|expliqué|ap] la leçon.
Nous avons [mesurer|mesuré|ap] la table.
Les savants ont [découvrir|découvert|ap] une étoile.
Le roi a [signer|signé|ap] la lettre.
Maël a [lancer|lancé|ap] le ballon.
Papa a [préparer|préparé|ap] le repas.
J'ai [ranger|rangé|ap] ma chambre.
Il a [casser|cassé|ap] un verre.
Elle a [chanter|chanté|ap] une chanson.
Nous avons [visiter|visité|ap] un musée.
Je vais à la piscine pour [nagé|nager|pi].
Il est l'heure de [mangé|manger|pi].
Elle est partie sans [parlé|parler|pi].
J'ai envie de [joué|jouer|pi] dehors.
Ce problème est facile à [trouvé|trouver|pi].
On a fini de [rangé|ranger|pi] la classe.
Il commence à [chanté|chanter|pi] très fort.
Nous nous entraînons pour [gagné|gagner|pi] la course.
Le bébé essaie de [marché|marcher|pi].
Il faut de l'eau pour [arrosé|arroser|pi] les [fleur|fleurs|dn].
Les élèves apprennent à [compté|compter|pi] jusqu'à cent.
On utilise une loupe pour [observé|observer|pi] les fourmis.
Il est interdit de [touché|toucher|pi] les tableaux.
Ne réponds pas sans [réfléchi|réfléchir|pi].
Elle a oublié de [fermé|fermer|pi] la porte.
Nous avons décidé de [partir|partir|pi] tôt.
Il [à|a|fa] peur de [tombé|tomber|pi].
Tom aide sa sœur à [porté|porter|pi] son sac.
J'ai appris à [dessiné|dessiner|pi] un cheval.
Il pleut sans [arrêté|arrêter|pi].
Le chien attend pour [sorti|sortir|pi].
Je n'ai pas le temps de [joué|jouer|pi].
Ce soir, je vais [mangé|manger|mi] des pâtes.
Tu peux [venu|venir|mi] avec moi.
Il veut [joué|jouer|mi] au foot.
On doit [parti|partir|mi] maintenant.
Nous allons [regardé|regarder|mi] le spectacle.
Ma sœur sait [nagé|nager|mi].
Vous pouvez [entré|entrer|mi] dans la classe.
Il faut [rangé|ranger|mi] les jouets.
Demain, je veux [allé|aller|mi] à la plage.
Les élèves doivent [écouté|écouter|mi] la consigne.
On peut [observé|observer|mi] la lune ce soir.
Il faut [protégé|protéger|mi] la nature.
Nous devons [mesuré|mesurer|mi] la cour.
Les plantes ne peuvent pas [poussé|pousser|mi] sans eau.
Tu vas [dessiné|dessiner|mi] un arbre.
Les explorateurs veulent [découvert|découvrir|mi] une île.
Il faut [recyclé|recycler|mi] les bouteilles.
Je vais [lavé|laver|mi] mes mains.
Elle veut [chanté|chanter|mi] une chanson.
Nous allons [préparé|préparer|mi] un gâteau.
Tu dois [rangé|ranger|mi] ton bureau.
Ils vont [arrivé|arriver|mi] bientôt.
Mon frère [à|a|fa] un nouveau vélo.
Elle [à|a|fa] mal à la tête.
On va [a|à|fa] la piscine.
Léa [à|a|fa] perdu son bonnet.
Je pense [a|à|fa] mes vacances.
Il y [à|a|fa] beaucoup de monde.
On [à|a|fa] gagné le match.
Il joue [a|à|fa] la balle avec son chien.
Elle parle [a|à|fa] sa maman.
Le village se trouve [a|à|fa] côté du volcan.
La girafe [à|a|fa] un long cou.
Le roi [à|a|fa] une couronne en or.
Nous allons [a|à|fa] l'école à pied.
Le chevalier [à|a|fa] une épée.
Ma cousine habite [a|à|fa] la campagne.
L'abeille [à|a|fa] six pattes.
Tom [à|a|fa] froid aux mains.
J'ai donné un dessin [a|à|fa] mon frère.
Il [à|a|fa] neigé cette nuit.
C'est [a|à|fa] toi de jouer.
Rayan [à|a|fa] onze ans.
On mange [a|à|fa] midi.
Il [et|est|fe] content de son cadeau.
Elle [et|est|fe] très gentille.
C'[et|est|fe] mon anniversaire demain.
Tom [et|est|fe] malade aujourd'hui.
Mon frère [et|est|fe] plus grand que moi.
J'aime le pain [est|et|fe] le fromage.
J'ai acheté des pommes [est|et|fe] des poires.
Il a faim [est|et|fe] soif.
Ce gâteau [et|est|fe] délicieux.
La lune [et|est|fe] pleine ce soir.
Le soleil [et|est|fe] une étoile.
Un volcan [et|est|fe] une montagne qui crache du feu.
Il fait chaud [est|et|fe] beau.
L'eau [et|est|fe] froide ce matin.
Le chat [et|est|fe] le chien jouent ensemble.
La baleine [et|est|fe] un mammifère.
Léa [est|et|fe] Tom sont dans la même classe.
Le ciel [et|est|fe] tout bleu.
Le cheval [et|est|fe] un animal rapide.
Elle chante [est|et|fe] elle danse.
Ma maison [et|est|fe] près de l'école.
Il pleut [est|et|fe] il fait froid.
Mes amis [son|sont|fs] gentils.
Elles [son|sont|fs] dans la cour.
Il range [sont|son|fs] vélo dans le garage.
Elle prend [sont|son|fs] cartable.
Ils [son|sont|fs] en retard.
Tom a perdu [sont|son|fs] bonnet.
Ils ne [son|sont|fs] pas venus.
Les chats [son|sont|fs] sur le toit.
Ma sœur aime [sont|son|fs] chien.
Les volcans [son|sont|fs] dangereux.
Le roi porte [sont|son|fs] manteau rouge.
Les planètes [son|sont|fs] très loin.
L'oiseau construit [sont|son|fs] nid.
Les dinosaures [son|sont|fs] très grands.
Chaque élève ouvre [sont|son|fs] livre.
Ces animaux [son|sont|fs] rares.
Le chevalier prend [sont|son|fs] épée.
Noah fait [sont|son|fs] lit tout seul.
Les fleurs [son|sont|fs] belles.
Mes parents [son|sont|fs] au travail.
Le chien mange [sont|son|fs] os.
Les enfants [son|sont|fs] contents.
Ils [on|ont|fo] un chat [noire|noir|pa].
Elles [on|ont|fo] gagné la course.
Mes parents [on|ont|fo] une nouvelle voiture.
Demain, [ont|on|fo] va au zoo.
Les enfants [on|ont|fo] faim.
Après l'école, [ont|on|fo] joue au parc.
Ils n'[on|ont|fo] pas de devoirs.
Est-ce qu'[ont|on|fo] peut sortir ?
Quand [ont|on|fo] est malade, on reste au lit.
Les soldats [on|ont|fo] construit un mur.
Les abeilles [on|ont|fo] des ailes.
Autrefois, [ont|on|fo] écrivait avec une plume.
Les volcans [on|ont|fo] un cratère.
Les élèves [on|ont|fo] fini leur travail.
En classe, [ont|on|fo] lève le doigt.
Les savants [on|ont|fo] observé le ciel.
Avec une loupe, [ont|on|fo] voit mieux.
Mes cousins [on|ont|fo] un grand jardin.
Le samedi, [ont|on|fo] fait des [crêpe|crêpes|dn].
Les oiseaux [on|ont|fo] des plumes.
Ce soir, [ont|on|fo] regarde un film.
Les chats [on|ont|fo] des moustaches.
Il [ses|sait|fc] faire du vélo.
Elle [ces|sait|fc] lire.
Tom range [ces|ses|fc] affaires.
Regarde [ses|ces|fc] nuages.
On ne [ces|sait|fc] pas.
Le roi part avec [ces|ses|fc] [chevalier|chevaliers|dn].
J'aime beaucoup [ses|ces|fc] fleurs.
Le savant ne [ses|sait|fc] pas la réponse.
Le chevalier prend [ces|ses|fc] armes.
Léa a oublié [ces|ses|fc] lunettes.
Il [ces|sait|fc] compter jusqu'à mille.
Qui a fait [ses|ces|fc] dessins ?
Elle [ses|sait|fc] où est le trésor.
Gabriel prête [ces|ses|fc] jeux à son frère.
Tu vois [ses|ces|fc] oiseaux ?
Mon frère [ses|sait|fc] nager.
Zoé aime [ces|ses|fc] parents.
[Ses|Ces|fc] arbres sont très vieux.
Le chat lèche [ces|ses|fc] pattes.
Personne ne [ses|sait|fc] la réponse.
Inès arrose [ces|ses|fc] plantes.
[Ces|Ses|fc] parents habitent loin.
Les [enfant|enfants|dn] [mange|mangent|ns] à la cantine.
Ils [son|sont|fs] [parti|partis|ep] tôt.
Elle [à|a|fa] [mangé|mangé|ap] une glace.
Mes [copain|copains|dn] [on|ont|fo] [gagner|gagné|ap] le match.
Il [et|est|fe] [allé|allé|ep] à l'école.
Elle [et|est|fe] [allé|allée|ep] à la plage.
Les [fille|filles|dn] [joue|jouent|ns] dans la cour.
Ils [on|ont|fo] [manger|mangé|ap] des frites.
Nous [allon|allons|sv] [mangé|manger|mi] chez mamie.
Le chien [est|et|fe] le chat [son|sont|fs] amis.
Tom [à|a|fa] [oublier|oublié|ap] [sont|son|fs] sac.
Les [oiseau|oiseaux|dn] [chante|chantent|ns] dans les arbres.
Elles [son|sont|fs] [content|contentes|ep] de partir.
Nous avons [oublier|oublié|ap] de [fermé|fermer|pi] la porte.
Les [élève|élèves|dn] [doive|doivent|ns] [écouté|écouter|mi] la maîtresse.
Il [à|a|fa] une [petit|petite|ag] sœur.
Ils [veut|veulent|sv] [joué|jouer|mi] au foot.
Ma [grand|grande|ag] sœur [et|est|fe] au collège.
Les [vache|vaches|dn] [mange|mangent|ns] de l'herbe [vert|verte|pa].
On [à|a|fa] [trouver|trouvé|ap] un chaton.
Les volcans [son|sont|fs] des montagnes [dangereuse|dangereuses|pa].
Elle [et|est|fe] [parti|partie|ep] sans [dire|dire|pi] au revoir.
Ils [on|ont|fo] des chaussures [neuve|neuves|pa].
Nous [somme|sommes|sv] [arrivé|arrivés|ep] en retard.
Le roi [à|a|fa] [perdre|perdu|ap] [sa|sa|dn] couronne.
Les [pirate|pirates|dn] [cherche|cherchent|ns] un trésor.
Il [faut|faut|mi] [rangé|ranger|mi] la classe.
Les feuilles [mort|mortes|pa] [tombe|tombent|ns] en automne.
Elles [son|sont|fs] [allé|allées|ep] au cinéma.
Tu [à|as|fa] [fini|fini|ap] tes devoirs ?
Mes [ami|amis|dn] [son|sont|fs] très [gentil|gentils|pa].
Il [veut|veut|mi] [allé|aller|mi] [a|à|fa] la piscine.
La [petit|petite|ag] fille [et|est|fe] [tombé|tombée|ep].
Les [baleine|baleines|dn] [vive|vivent|ns] dans l'océan.
On [à|a|fa] [visiter|visité|ap] un château.
Ils [peut|peuvent|sv] [venu|venir|mi] demain.
Ces [fleur|fleurs|dn] [son|sont|fs] [jolie|jolies|pa].
Elle [à|a|fa] [ranger|rangé|ap] [ces|ses|fc] jouets.
Les [chat|chats|dn] [dort|dorment|ns] au soleil.
Nous [voulon|voulons|sv] [joué|jouer|mi] dehors.
La porte [et|est|fe] [fermé|fermée|ep].
Les [enfant|enfants|dn] [on|ont|fo] [dessiner|dessiné|ap] un arbre.
Il [et|est|fe] [arrivé|arrivé|ep] le [premier|premier|ag].
Mes [sœur|sœurs|dn] [son|sont|fs] [parti|parties|ep].
Tu [doit|dois|sv] [rangé|ranger|mi] ta chambre.
Les [abeille|abeilles|dn] [fabrique|fabriquent|ns] du miel.
Elle [à|a|fa] une robe [bleu|bleue|pa].
Les [élève|élèves|dn] [on|ont|fo] [fini|fini|ap] la dictée.
Il [et|est|fe] temps de [parti|partir|pi].
Ils [son|sont|fs] [venu|venus|ep] [a|à|fa] la fête.
Nous [avon|avons|sv] [vu|vu|ap] des [étoile|étoiles|dn].
Ma [nouveau|nouvelle|ag] maîtresse [et|est|fe] gentille.
Les [loup|loups|dn] [hurle|hurlent|ns] la nuit.
On [va|va|mi] [mangé|manger|mi] [a|à|fa] midi.
Elle [et|est|fe] [resté|restée|ep] [a|à|fa] la maison.
Les [plante|plantes|dn] [pousse|poussent|ns] au soleil.
Il [à|a|fa] [casser|cassé|ap] [sont|son|fs] jouet.
Ils [on|ont|fo] peur de [tombé|tomber|pi].
Les [poule|poules|dn] [pond|pondent|ns] des œufs [frai|frais|pa].
Tu [peut|peux|sv] [venu|venir|mi] avec nous.
Elles [son|sont|fs] [fatigué|fatiguées|ep] après la course.
Le [petit|petit|ag] garçon [et|est|fe] [tombé|tombé|ep].
On [à|a|fa] [lu|lu|ap] une [long|longue|ag] histoire.
Les [chien|chiens|dn] [aboie|aboient|ns] fort.
Je [veut|veux|sv] [apprendre|apprendre|mi] [a|à|fa] nager.
Ses [ami|amis|dn] [son|sont|fs] [venu|venus|ep] le voir.
Il [faut|faut|mi] [protégé|protéger|mi] [ces|ses|fc] yeux du soleil.
Les [fourmi|fourmis|dn] [transporte|transportent|ns] des miettes.
Elle [et|est|fe] [né|née|ep] en hiver.
Les [nuage|nuages|dn] [son|sont|fs] [gris|gris|pa].
Ils [veut|veulent|sv] [construire|construire|mi] une cabane.
On [à|a|fa] [préparer|préparé|ap] une [bon|bonne|ag] soupe.
Mes [parent|parents|dn] [on|ont|fo] [acheter|acheté|ap] un chien.
Les [dauphin|dauphins|dn] [saute|sautent|ns] hors de l'eau.
Elle [et|est|fe] [monté|montée|ep] sur le toit.
Tu [à|as|fa] [oublier|oublié|ap] ton manteau.
Ils [son|sont|fs] [sorti|sortis|ep] sans [mangé|manger|pi].
Les [lapin|lapins|dn] [mange|mangent|ns] des carottes.
Il [et|est|fe] [allé|allé|ep] [a|à|fa] la ferme.
Nous [pouvon|pouvons|sv] [joué|jouer|mi] ensemble.
Les [étoile|étoiles|dn] [brille|brillent|ns] la nuit.
Elle [à|a|fa] [chanter|chanté|ap] avec [ces|ses|fc] amies.
Les [tortue|tortues|dn] [marche|marchent|ns] lentement.
Ils [on|ont|fo] [perdre|perdu|ap] leur ballon.
La [grand|grande|ag] tour [et|est|fe] [tombé|tombée|ep].
Une [petit|petite|ag] souris [et|est|fe] [caché|cachée|ep] sous le lit.
Les fleurs [rouge|rouges|pa] [son|sont|fs] pour [ces|ses|fc] grands-parents.
Elle [à|a|fa] une [joli|jolie|ag] voix [doux|douce|pa].
Après la pluie, [ont|on|fo] va [cherché|chercher|pi] des escargots.
Il [ses|sait|fc] [écrire|écrire|ap] [sont|son|fs] prénom.
Ma [vieux|vieille|ag] voisine [à|a|fa] trois chats [noir|noirs|pa].
Les élèves [on|ont|fo] envie de [chanté|chanter|pi].
[Ont|On|fo] a [planter|planté|ap] des graines [vert|vertes|pa].
Elle [ses|sait|fc] [dessiné|dessiner|mi] des chevaux.
Il a pris une [grand|grande|ag] boîte pour [rangé|ranger|pi] ses jouets.
"""


def parse(line):
    out, errors, pos, gold = [], [], 0, []
    for m in re.finditer(r"\[([^|\]]+)\|([^|\]]+)\|([a-z]+)\]", line):
        out.append(line[pos:m.start()])
        gold.append(line[pos:m.start()])
        typed, g, c = m.group(1), m.group(2), CLS[m.group(3)]
        start = sum(len(x) for x in out)
        out.append(typed)
        gold.append(g)
        if typed != g:
            errors.append({"start": start, "end": start + len(typed), "typed": typed, "gold": g, "class": c})
        pos = m.end()
    out.append(line[pos:])
    gold.append(line[pos:])
    return "".join(out), "".join(gold), errors


lines = [l.strip() for l in S.strip().splitlines() if l.strip()]
recs, seen = [], set()
for l in lines:
    text, gold, errors = parse(l)
    if not errors or text in seen:
        continue
    seen.add(text)
    recs.append({"id": "gi-%03d" % (len(recs) + 1), "src": "invented", "licence": "GPL-3.0-or-later", "text": text,
                 "gold": gold, "errors": errors})
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write(json.dumps({"note": "phrases inventées pour Pépite, GPL-3.0-or-later",
                        "about": "G-invented (plan 11, 10D-0): sentences written for this project at school level "
                                 "(CE2–6e), one to four errors each; classes = the P-agreement categories; names only "
                                 "from personal-fixture.json; no pupil text. Format: id, src, licence, text (as typed), "
                                 "gold, errors [start, end, typed, gold, class] (offsets in text)."},
                       ensure_ascii=False) + "\n")
    for r in recs:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
cnt, multi = {}, 0
for r in recs:
    multi += len(r["errors"]) >= 2
    for e in r["errors"]:
        cnt[e["class"]] = cnt.get(e["class"], 0) + 1
print(len(recs), "sentences;", multi, "with 2+ errors;", sum(cnt.values()), "errors", dict(sorted(cnt.items())))
