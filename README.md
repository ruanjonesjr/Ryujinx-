# Ryujinx Android

Projeto independente para adaptar o código-fonte original do Ryujinx ao Android ARM64.

## Fonte original

O arquivo `ryujinx-mirror-mirror-master.zip` contém a fonte-base. Ele é mantido separado de qualquer frontend ou port anterior.

## Estado real do projeto

- [x] Repositório novo, sem histórico de arquivos de outros projetos.
- [x] Arquivo-fonte original presente no repositório.
- [x] Workflow de auditoria criado para validar o ZIP e inventariar os projetos C# reais.
- [ ] Auditoria do conteúdo do ZIP concluída pelo GitHub Actions.
- [ ] Portabilidade do núcleo HLE/CPU/memória para Android validada.
- [ ] Backend Vulkan Android integrado à superfície nativa.
- [ ] Áudio e entrada Android integrados.
- [ ] APK ARM64 compilado e testado em dispositivo físico.

## Próximo passo automático

O workflow **Ryujinx source audit** extrai e valida o ZIP, localiza os projetos essenciais (HLE, GAL, Vulkan, Audio, CPU e ARMeilleure) e publica um relatório como artefato do GitHub Actions.

Não considerar o projeto um emulador funcional nem distribuir um APK como jogável até que HLE, renderer Vulkan Android, áudio, controles e ciclo de vida estejam conectados e testados.

## Dispositivo inicial de validação

Redmi Note 12S — MediaTek Helio G96, GPU Mali-G57 MC2, ABI ARM64.
