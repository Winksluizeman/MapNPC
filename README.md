Een eigen AI-NPC-engine in Unity. Theo Verbeek (58) verkoopt zijn huis terwijl hij eigenlijk niet weg wil; zijn verhaal loopt in vier fases die reageren op hoe de speler met hem omgaat. Het denken en praten draait volledig op de eigen computer.

Wat erin zit/Bestand	Wat het doet

TheoBrain.cs	Gesprekslogica: prompt opbouwen, Ollama aanroepen, fase kiezen, elke beurt loggen in een CSV
TheoChatUI.cs	Invoerveld, scrollbaar gesprek en een debugregel met de actieve fase en emotie
TheoVoice.cs	Stuurt elk antwoord naar Piper, zet de WAV om naar een AudioClip en speelt die af
theo_story.json	Het personage en zijn verhaal: beschrijving, praatwijze, regels, vier fases, overgangen en trefwoorden
theo_basis.json	Dezelfde Theo zonder verhaal, als vergelijking


Hoe een beurt werkt
1.	Trefwoorden. Bevat het bericht een vast woord zoals “doei” of “ik ga ervandoor”, dan gaat de overgang direct af.
2.	Beoordelen. Anders kiest het taalmodel, met temperature 0, welke overgang uit de huidige fase past.
3.	Antwoorden. Theo antwoordt in de (eventueel nieuwe) fase, met een emotie uit het wiel van Plutchik.
Het taalmodel (gemma3:4b) draait via Ollama op localhost:11434, de stem (nl_NL-ronnie-medium) via Piper op localhost:5000. Een schema staat in bijlage C van het onderzoek en in het product Architectuurontwerpen.

Installeren
4.	Installeer Ollama en voer uit: ollama pull gemma3:4b.
5.	Installeer Python 3.12 en Piper: py -m pip install "piper-tts[http]", daarna py -m piper.download_voices nl_NL-ronnie-medium en py -m piper.http_server -m nl_NL-ronnie-medium.
6.	Zet de bestanden in Assets/TheoEngine van een Unity-project (getest met Unity 6000.6.4f1).
7.	Maak een GameObject met TheoBrain en TheoVoice, en sleep theo_story.json in Story Json.
8.	Maak een Canvas met een TMP-invoerveld, een knop, een Scroll View voor het gesprek en een debugtekst, en koppel die aan TheoChatUI.
9.	Zet zo nodig in Project Settings → Player de optie Allow downloads over HTTP op Always allowed.

Logging
Bij elke start schrijft TheoBrain een CSV naar %USERPROFILE%\AppData\LocalLow\<Company>\<Product>\, met per beurt: tijd, model, fase voor en na, gekozen overgang, methode (trefwoord of llm), emotie, beoordelingstijd, totale tijd en de tekst.
Bekende beperkingen
•	Op een laptop met een geïntegreerde GPU duurt een antwoord 3 tot 21 seconden; de streefwaarde van 2 seconden wordt niet gehaald.
•	Met gemma3:4b kloppen 3 tot 6 van de 10 overgangen in de vaste test.
•	Geen spraakherkenning en geen lipsync; die zitten in de Convai-versie.
