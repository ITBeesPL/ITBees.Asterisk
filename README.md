# ITBees.Asterisk

Biblioteka do sterowania centralką Asterisk działającą na tej samej maszynie co aplikacja (słupek, kasa albo serwer Octoparka). Konfiguracja jest deklaratywna: aplikacja trzyma „paczkę” (numery wewnętrzne, grupy, trunki), a biblioteka doprowadza do niej Asteriska i odczytuje jego stan.

## Co potrafi

- **Paczka konfiguracji** (`AsteriskConfigurationPackage`, JSON): numery wewnętrzne PJSIP, grupy dzwonienia, interkom z automatycznym odbiorem, hotline, trunki SIP, trasy wychodzące, porty SIP/RTP, NAT, WebRTC.
- **Walidacja** (`package.Validate()`): błędy i ostrzeżenia. Odrzuca też każdą wartość, która mogłaby „wyjść” ze swojej linii w pliku konfiguracyjnym (nowa linia, `;`, nawiasy), bo wartości pochodzą z formularza.
- **Generowanie plików**: `pjsip.conf`, `extensions.conf`, `manager.conf`, `rtp.conf`, `http.conf`.
- **Plan i zastosowanie** (`PlanAsync` / `ApplyAsync`): zapisuje tylko pliki, które się różnią, wyłącza stary sterownik `chan_sip`, przeładowuje albo restartuje usługę i sprawdza przez AMI, czy Asterisk załadował wszystkie konta. Drugie zastosowanie tej samej paczki niczego nie zmienia.
- **Host**: instalacja pakietu `asterisk` (apt), `systemctl start/stop/restart/enable`, polecenia `asterisk -rx`.
- **AMI** (`AmiConnection`): stan centralki, zarejestrowane telefony (adres, model, czas odpowiedzi), trwające rozmowy, rejestracje trunków, zestawienie połączenia (`Originate`), rozłączenie, polecenia CLI.
- **Karta konfiguracji telefonu** (`PhoneSetupSheet`): co wpisać w urządzeniu i gdzie, osobno dla Fanvil PA2S, Fanvil X3SP, Yealink SIP-T30P i telefonu w przeglądarce.

## Użycie

```csharp
services.AddAsterisk();   // IAsteriskService, singleton

var package = new AsteriskConfigurationPackage();
package.Manager.Password = "losowe-haslo-ami";
package.Extensions.Add(new AsteriskExtension { Number = "100", Name = "Kasa", Password = "...", DeviceModel = AsteriskDeviceModel.YealinkT30p });
package.Extensions.Add(new AsteriskExtension { Number = "200", Name = "Wjazd", Password = "...", DeviceModel = AsteriskDeviceModel.FanvilPa2s, HotlineTarget = "600" });
package.RingGroups.Add(new AsteriskRingGroup { Number = "600", Name = "Obsługa", Members = { "100" } });

var plan = await asterisk.PlanAsync(package);          // co się zmieni, nic nie zapisuje
var result = await asterisk.ApplyAsync(package, new AsteriskProvisioningOptions { InstallIfMissing = true });
var status = await asterisk.GetStatusAsync(package.Manager);
await asterisk.OriginateAsync(package.Manager, "100", "200");   // dzwoni 100, po odebraniu łączy z 200
```

## Jak to działa na hoście

- Biblioteka **jest właścicielem** pięciu plików w `/etc/asterisk` i zapisuje je w całości. Plik, którego wcześniej nie zapisała, zostaje raz skopiowany jako `<nazwa>.itbees-orig`. Ręczne dodatki należą do `pjsip_custom.conf` i `extensions_custom.conf` (dołączane przez `#tryinclude`).
- W `modules.conf` dopisuje tylko `noload => chan_sip.so`, bo stary sterownik zająłby port SIP.
- Polecenia uprzywilejowane idą przez `sudo -n` (konto aplikacji musi mieć sudo bez hasła); jako root biblioteka pomija sudo. Pliki dostają właściciela `asterisk` i prawa `640`, bo zawierają hasła.
- Zmiana numerów, grup i tras kończy się `core reload` (rozmowy trwają dalej). Zmiana portów, transportów, RTP, serwera HTTP, AMI albo listy modułów wymaga restartu usługi.
- AMI słucha domyślnie tylko na `127.0.0.1`.
- Wyniki planu i zastosowania nigdy nie zawierają haseł ani linii z plików, tylko nazwy plików i liczbę zmienionych linii.

## Funkcje centralki

| Funkcja | Jak |
|---|---|
| Rozmowa między numerami | wybranie numeru wewnętrznego |
| Grupa dzwonienia | numer grupy; wszyscy naraz albo po kolei |
| Brak odpowiedzi | `NoAnswerTarget` numeru lub grupy (pętle odrzuca walidacja) |
| Interkom | prefiks (domyślnie `*80`) + numer: urządzenie odbiera samo |
| Automatyczny odbiór | `AutoAnswer` numeru (głośnik PA2S, panel) |
| Hotline | `HotlineTarget`: cokolwiek urządzenie wybierze, połączenie trafia pod wskazany numer |
| Wyjście na miasto | trunk + trasa (`Pattern`, `StripDigits`, `Prefix`) |
| Połączenia przychodzące | `InboundTarget` trunka; bez niego są odrzucane |
| Telefon w przeglądarce | `WebRtc.Enabled` + numer z `DeviceModel = WebRtcBrowser`, adres `ws://host:8088/ws` |

## Ograniczenia

- Linux z systemd i apt. Debian 12 nie ma pakietu `asterisk` w repozytoriach: `InstallAsync` zgłasza to wprost i Asteriska trzeba wtedy zainstalować ręcznie.
- Klient AMI wykonuje jedną akcję naraz i nie nasłuchuje zdarzeń.
- WebRTC używa certyfikatu generowanego przez Asteriska (`dtls_auto_generate_cert`) i nieszyfrowanego `ws://`, więc strona z telefonem musi być otwarta z `http://localhost` albo adresu w sieci lokalnej bez HTTPS.
- Biblioteka nie generuje plików autoprovisioningu telefonów; karta konfiguracji podaje wartości do wpisania ręcznie.

## Testy

`dotnet test` — renderowanie plików, walidacja, klient AMI na atrapie serwera TCP, provisioning na atrapie hosta. Testy nie potrzebują Asteriska.
