from mythic_payloadtype_container.MythicCommandBase import *
import json
from mythic_payloadtype_container.MythicRPC import *
import base64

class RegisterFileArguments(TaskArguments):

    def __init__(self, command_line):
        super().__init__(command_line)
        self.args = {
            "file": CommandParameter(name="File", type=ParameterType.File)
        }

    async def parse_arguments(self):
        if len(self.command_line) == 0:
            raise Exception("No arguments given.")
        if self.command_line[0] != "{":
            raise Exception("Require JSON blob, but got raw command line.")
        self.load_args_from_json_string(self.command_line)
        pass


class RegisterFileCommand(CommandBase):
    cmd = "register_file"
    needs_admin = False
    help_cmd = "register_assembly (modal popup)"
    description = "Register a file to later use in the agent."
    version = 2
    is_exit = False
    is_file_browse = False
    is_process_list = False
    is_download_file = False
    is_upload_file = False
    is_remove_file = False
    author = "@djhohnstein"
    argument_class = RegisterFileArguments
    attackmapping = ["T1547"]

    async def create_tasking(self, task: MythicTask) -> MythicTask:
        original_file_name = json.loads(task.original_params)['File']
        resp = await MythicRPC().execute("create_file",
                                          task_id=task.id,
                                          file=base64.b64encode(task.args.get_arg("file")).decode(),
                                          saved_file_name=original_file_name,
                                          delete_after_fetch=False)
        if resp.status == MythicStatus.Success:
            task.args.add_arg("file_id", resp.response['agent_file_id'])
            task.args.add_arg("file_name", original_file_name)
            task.args.remove_arg("file")
        else:
            raise Exception(f"Failed to host assembly: {resp.error}")
        task.display_params = original_file_name
        return task

    async def process_response(self, response: AgentResponse):
        pass