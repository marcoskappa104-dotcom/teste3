Um dos principais diferenciais do jogo é o sistema de habilidades baseado em um item especial chamado **Joia do Poder**.
Em vez das skills serem aprendidas diretamente pelo personagem através de árvore de talentos tradicional, as habilidades são concedidas dinamicamente através dessas joias equipáveis.

Esse sistema é inspirado em mecânicas de customização profundas presentes em ARPGs como Path of Exile, permitindo enorme liberdade de builds, combinações e progressão de personagem.

---

# Sistema de Joias do Poder

As **Joias do Poder** são itens especiais obtidos pelo jogador ao longo da progressão do jogo.

Elas podem ser adquiridas através de:

* Drop de monstros
* Recompensas de quests
* Dungeons
* Bosses
* Eventos
* Crafting
* NPCs especiais
* Marketplace/Trade entre jogadores
* Sistema de conquistas
* Raids e conteúdos endgame

---

# Funcionamento do Sistema

O jogador possui uma interface específica dedicada ao gerenciamento das Joias do Poder.

Ao equipar uma joia em um slot válido da interface:

* a habilidade é desbloqueada automaticamente
* a skill é adicionada à barra de habilidades
* os atributos da skill passam a ser calculados em tempo real
* efeitos visuais e animações são habilitados
* o servidor sincroniza a nova habilidade para todos os clientes

Ao remover a joia:

* a habilidade é desativada
* removida da barra de skills
* cooldowns são cancelados
* buffs relacionados podem ser removidos

---

# Objetivos do Sistema

O sistema foi criado para:

* aumentar a liberdade de build
* permitir combinações únicas
* incentivar exploração e farm
* criar raridade e economia entre jogadores
* tornar equipamentos parte central da progressão
* evitar classes extremamente limitadas
* permitir diversidade de gameplay

---

# Estrutura das Joias

Cada Joia do Poder possui propriedades próprias.

---

## Informações básicas

* Nome
* Ícone
* Raridade
* Tipo
* Tier
* Descrição
* Peso
* Valor

---

## Dados da habilidade vinculada

* Skill concedida
* Tipo da skill
* Dano base
* Escalabilidade
* Consumo de mana
* Cooldown
* Tempo de conjuração
* Range
* Área de efeito
* Quantidade de alvos
* Elemento

---

# Tipos de Joias

---

## Joias de habilidade ativa

Concedem habilidades utilizáveis diretamente pelo jogador.

Exemplos:

* Bola de fogo
* Investida
* Cura
* Flechas múltiplas
* Invocação
* Explosão elemental

---

## Joias passivas

Concedem bônus permanentes enquanto equipadas.

Exemplos:

* +Velocidade de ataque
* +Chance crítica
* Regeneração de mana
* Resistência elemental
* Roubo de vida

---

## Joias de suporte/modificação

Modificam habilidades já existentes.

Exemplos:

* skill causa fogo
* skill ricocheteia
* skill explode ao impacto
* skill ganha stun
* redução de cooldown
* aumento de área

---

# Sistema de Slots

A interface de Joias possui slots específicos.

Possíveis tipos:

* Slot ofensivo
* Slot defensivo
* Slot utilitário
* Slot universal
* Slot lendário

---

# Sistema de raridade

As Joias podem possuir raridades diferentes:

* Comum
* Mágica
* Rara
* Épica
* Lendária
* Mítica

A raridade influencia:

* poder da skill
* quantidade de modificadores
* efeitos especiais
* escalabilidade
* aparência visual

---

# Progressão das Joias

As Joias também podem evoluir.

---

## Possíveis sistemas futuros

* Level da joia
* Experiência própria
* Refinamento
* Despertar
* Evolução de tier
* Fusão de joias
* Socketing
* Encantamentos
* Corrupção
* Upgrade de qualidade

---

# Integração com Multiplayer (Mirror)

Como o jogo é multiplayer online, o sistema precisa funcionar de forma totalmente sincronizada e segura.

---

# Pontos importantes no networking

O servidor deve possuir autoridade total sobre:

* equipar/remover joias
* desbloqueio de skills
* validação de slots
* cooldowns
* dano
* efeitos
* buffs/debuffs

---

# Fluxo ideal no Mirror

### Cliente

O jogador tenta equipar uma joia.

↓

### `Command`

O cliente envia solicitação ao servidor.

↓

### Servidor

O servidor valida:

* existência da joia
* slot válido
* requisitos
* inventário
* possíveis exploits

↓

### Servidor aplica

* skill desbloqueada
* SyncVars atualizadas
* lista de habilidades sincronizada

↓

### `ClientRpc`

Todos os clientes recebem atualização visual.

---

# Estrutura recomendada de scripts

---

## Sistema de dados

### `PowerGemData`

Responsável pelos dados da joia:

* ID
* raridade
* skill vinculada
* tier
* ícone
* modificadores

---

## Sistema de skill

### `SkillData`

Define:

* dano
* cooldown
* cast
* efeitos
* animações
* tipo da skill

---

## Sistema runtime

### `PlayerSkillManager`

Responsável por:

* skills equipadas
* cooldowns
* execução
* sincronização

---

## Sistema de Joias

### `PowerGemManager`

Responsável por:

* equipar
* remover
* validar
* sincronizar slots

---

## Interface

### `PowerGemUI`

Gerencia:

* drag and drop
* visual dos slots
* tooltip
* equipar/remover

---

# Possibilidades futuras extremamente importantes

---

# Build System

Permitir builds totalmente diferentes entre jogadores.

Exemplo:

* Guerreiro elemental
* Arqueiro mágico
* Tank com magia
* Necromante melee
* Assassino híbrido

---

# Economia do jogo

As Joias podem se tornar:

* principal item de trade
* item raro de endgame
* núcleo da economia
* motivação de farm

---

# Conteúdo endgame

Possibilidades:

* Joias exclusivas de boss
* Joias únicas
* Joias sazonais
* Joias corrompidas
* Joias ancestrais
* Sistema de combinação

---

# Segurança importante

Como o sistema impacta diretamente o combate online, é extremamente importante evitar:

* duplicação de joias
* equipar joias inexistentes
* hacks de skill
* bypass de cooldown
* manipulação de dano
* skill injection

Tudo isso deve ser validado server-side.

---

# Objetivo do sistema

O sistema de Joias do Poder foi criado para transformar as habilidades em um elemento central de progressão e personalização do jogo, permitindo que cada jogador monte builds únicas, criando um RPG Online muito mais profundo, rejogável e estratégico.
