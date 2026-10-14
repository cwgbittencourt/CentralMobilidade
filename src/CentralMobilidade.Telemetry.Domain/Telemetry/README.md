# TelemetryEvent v1 — T043

`TelemetryEvent` é um record imutável validado no construtor. Identidade usa
`CompanyId`, `VehicleId` e `EventTimestamp` obrigatórios; os value objects existentes
preservam IDs e garantem UTC com precisão exata de milissegundos. `DriverId` opcional
é somente um identificador pseudônimo, sem nome, CPF ou resolução cadastral.

Latitude e longitude precisam ser finitas e estar nos intervalos inclusivos -90..90
e -180..180. Velocidade é double finito não negativo, preservado em km/h. RPM é long
não negativo; ignição é bool, portanto tipos textuais de Line Protocol serão validados
pelo parser na T046. O domínio não inventa limites superiores para velocidade/RPM.

Placa opcional é uma string já decodificada pelo parser. Preserva espaços e caracteres
escapados, normaliza somente para maiúsculas invariant e rejeita controles/Unicode
malformado. Não aplica formato de placa de um país nem um limite adicional não previsto
no contrato; tamanho da linha será limitado pelo leitor T045. Ausência é null.

As propriedades não possuem setters/init, evitando bypass de validação por `with`.
Igualdade compara todos os valores normalizados. `ToString()` retorna apenas
`[TelemetryEvent]`, e exceções não incluem placa, coordenadas ou valores inválidos.
Isso não substitui os filtros de logging/destructuring já existentes.

Não depende de Application, Contracts ou Infrastructure. Leitura/escaping de Line
Protocol, rejeições com códigos estáveis e fingerprint canônico pertencem à T046;
seu resultado/projeção poderá associar o digest ao evento validado. Esta etapa não
escolhe uma serialização canônica nem acrescenta um fingerprint provisório.

Validação: 27 novos testes de domínio passam (limites inclusivos, NaN/infinito,
negativos, identidade obrigatória, opcionais, Unicode, cultura, privacidade,
imutabilidade e igualdade). Os testes do parser T036 continuam vermelhos até T046.
