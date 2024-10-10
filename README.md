<p align="center">
  <img width="300" alt="Codeium" src="codeium.svg"/>
</p>

---

[![Discord](https://img.shields.io/discord/1027685395649015980?label=community&color=5865F2&logo=discord&logoColor=FFFFFF)](https://discord.gg/3XFf78nAx5)
[![Twitter Follow](https://img.shields.io/badge/style--blue?style=social&logo=twitter&label=Follow%20%40codeiumdev)](https://twitter.com/intent/follow?screen_name=codeiumdev)
![License](https://img.shields.io/github/license/Exafunction/CodeiumVisualStudio)
[![Docs](https://img.shields.io/badge/Codeium%20Docs-09B6A2)](https://docs.codeium.com)
[![Canny Board](https://img.shields.io/badge/Feature%20Requests-6b69ff)](https://codeium.canny.io/feature-requests/)
[![built with Codeium](https://codeium.com/badges/main)](https://codeium.com?repo_name=exafunction%2FCodeiumVisualStudio)

[![Visual Studio](https://img.shields.io/visual-studio-marketplace/d/Codeium.CodeiumVS?logo=visualstudio&label=Visual%20Studio)](https://marketplace.visualstudio.com/items?itemName=Codeium.CodeiumVS)
[![Visual Studio Code](https://img.shields.io/visual-studio-marketplace/d/Codeium.codeium?logo=visualstudiocode&label=Visual%20Studio%20Code)](https://marketplace.visualstudio.com/items?itemName=Codeium.codeium)
[![JetBrains](https://img.shields.io/jetbrains/plugin/d/20540?label=JetBrains)](https://plugins.jetbrains.com/plugin/20540-codeium/)
[![Open VSX](https://img.shields.io/open-vsx/dt/Codeium/codeium?label=Open%20VSX)](https://open-vsx.org/extension/Codeium/codeium)
[![Google Chrome](https://img.shields.io/chrome-web-store/users/hobjkcpmjhlegmobgonaagepfckjkceh?label=Google%20Chrome&logo=googlechrome&logoColor=FFFFFF)](https://chrome.google.com/webstore/detail/codeium/hobjkcpmjhlegmobgonaagepfckjkceh)

# Codeium for Visual Studio

[Codeium](https://codeium.com/) is the modern coding superpower, a free code acceleration toolkit built on cutting edge AI technology. Currently, Codeium provides autocomplete, chat, and search capabilities in 70+ languages, with lightning fast speeds and state-of-the-art suggestion quality.

There are many parts of the modern coding workflow that are boring, tedious, or frustrating, from regurgitating boilerplate to poring through StackOverflow. Recent advances in AI allow us to eliminate these parts, making it seamless to turn your ideas into code. With easy integration into Visual Studio and a less than 2 minute installation process, you can focus on being the best software developer, not the best code monkey.

With Codeium, you get:
- Unlimited single and multi-line code completions forever.
- IDE-integrated chat: no need to leave Visual Studio to ChatGPT, and use convenient suggestions such as [Refactor](#refactor) and [Explain](#explain).
- Support for 70+ programming languages: Javascript, Python, Typescript, PHP, Go, Java, C, C++, Rust, Ruby, and more.
- Support through our [Discord Community](https://discord.gg/3XFf78nAx5).

Join thousands of other developers who are using Codeium for free to accelerate their software iteration process, improve their code quality and consistency, reduce the number of code review iterations, accelerate developer onboarding, and keep them in their flow state. Want to learn more? Check out our [FAQ](https://codeium.com/faq).

Check out our [playground](https://www.codeium.com/playground) if you want to quickly try out Codeium online.

# Your New Autocomplete Superpowers
## Use Natural Language

Need to make changes in an unfamiliar language or codebase? Instead of spending hours learning all of the syntactical and stylistic details, type out comments in natural language and let Codeium get the job done.

<p align="center">
  <image src="docs/images/gen_random.gif" width="500"/>
</p>

## Minimize Boilerplate Coding

Sometimes feel like a code monkey copy-pasting and modifying boilerplate code over and over again? Let Codeium handle writing the repetitive work so that you can stay in the coding flow state, generating code faster than you can type.

<p align="center">
  <image src="docs/images/auto_complete.gif" width="500"/>
</p>

# Your New Chat Superpowers
## Generate

Codeium Chat can generate whole functions and applications for you with simple natural language prompting and constraints.

<p align="center">
  <image src="docs/images/chat_generate_function.gif" width="800"/>
</p>

## Explain

Diving into a foreign codebase? With the push of a button, our assistant will explain everything you need to know.

<p align="center">
  <image src="docs/images/chat_explain.gif" width="800"/>
</p>

## Refactor

Want to rewrite your existing code to add a new feature? Fix a bug? Make it prettier? Click a button and tell your Codeium assistant what you want.

<p align="center">
  <image src="docs/images/chat_refactor.gif" width="800"/>
</p>

## Translate

Leverage the power of AI to translate whole functions from one language to another within seconds.

<p align="center">
  <image src="docs/images/chat_convert.gif" width="800"/>
</p>

## Explain Problem

Ever feels like warnings and errors are too verbose? Codeium can explain like you're five!
<p align="center">
  <image src="docs/images/chat_explain_problem.gif" width="800"/>
</p>

# Build Instructions
- Open Visual Studio Installer and click the Modify button. Install the "Visual Studio extension development" in Other Toolsets.
- Install the [Extensibility Essentials 2022](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.ExtensibilityEssentials2022) extension.
- Open `Codeium.sln` and press <kbd>CTRL+B</kbd> to build, if there were no errors, there should be a `Codeium.vsix` file in the `CodeiumVS/bin` folder.
- You can press <kbd>F5</kbd> to debug the extension on a separate instance of Visual Studio. Consult the [official guide](https://learn.microsoft.com/en-us/visualstudio/extensibility/starting-to-develop-visual-studio-extensions?view=vs-2022) for more information.

# How to help
Feel free to [submit issues](https://github.com/Exafunction/CodeiumVisualStudio/issues/new) related to the extension so that we can better improve your experiences.

We greatly value your input and invite you to contribute to our project. To do so, kindly [initiate a pull request](https://github.com/Exafunction/CodeiumVisualStudio/compare). Your expertise and insights are much appreciated.

# Documentation
For more information on how to install and use the extension, please see [the documentation](/docs/).

# License
Codeium Extension for Visual Studio is licensed under the MIT License, see [LICENSE.txt](/LICENSE.txt) for more information.
