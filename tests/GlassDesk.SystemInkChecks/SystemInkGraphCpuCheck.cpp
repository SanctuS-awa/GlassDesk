#include <SystemGlassInk.h>

#include <array>
#include <cmath>
#include <cstdio>
#include <limits>
#include <vector>

namespace
{
    using namespace winrt;
    using namespace winrt::Windows::Foundation;
    using namespace winrt::Windows::Graphics::Effects;
    using namespace gd::systemglass::ink;
    using EffectInterop = ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop;
    using Mapping = ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING;

    struct SourceStub : implements<SourceStub, IGraphicsEffectSource> {};

    struct EffectNode final
    {
        GUID id{};
        com_ptr<EffectInterop> interop{ nullptr };
        UINT propertyCount{};
        UINT sourceCount{};
    };

    bool Check(bool condition, char const* name)
    {
        std::printf("%s: %s\n", condition ? "PASS" : "FAIL", name);
        return condition;
    }

    bool Walk(IGraphicsEffectSource const& source, std::vector<EffectNode>& nodes)
    {
        auto interop=source.try_as<EffectInterop>();
        if(!interop)return true; // Source parameters are deliberately non-effect leaves.

        GUID id{};
        if(FAILED(interop->GetEffectId(&id)))return false;
        for(auto const& existing:nodes)if(IsEqualGUID(existing.id,id))return true;

        UINT propertyCount{},sourceCount{};
        if(FAILED(interop->GetPropertyCount(&propertyCount))||
            FAILED(interop->GetSourceCount(&sourceCount)))return false;
        nodes.push_back({id,interop,propertyCount,sourceCount});

        for(UINT index=0;index<sourceCount;++index){
            ABI::Windows::Graphics::Effects::IGraphicsEffectSource* raw{};
            if(FAILED(interop->GetSource(index,&raw))||!raw)return false;
            IGraphicsEffectSource child{nullptr};
            copy_from_abi(child,raw);
            raw->Release();
            if(!Walk(child,nodes))return false;
        }
        return true;
    }

    EffectNode const* Find(std::vector<EffectNode> const& nodes, GUID const& id)
    {
        for(auto const& node:nodes)if(IsEqualGUID(node.id,id))return &node;
        return nullptr;
    }

    bool GetProperty(EffectNode const& node, UINT index, IPropertyValue& result)
    {
        ABI::Windows::Foundation::IPropertyValue* raw{};
        if(FAILED(node.interop->GetProperty(index,&raw))||!raw)return false;
        copy_from_abi(result,raw);
        raw->Release();
        return true;
    }

    bool MappingIs(EffectNode const& node, wchar_t const* name, UINT expectedIndex)
    {
        UINT index{};
        Mapping mapping{};
        return SUCCEEDED(node.interop->GetNamedPropertyMapping(name,&index,&mapping))&&
            index==expectedIndex&&
            mapping==ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
    }

    bool ValidateGraph(EffectGraph const& graph)
    {
        std::vector<EffectNode> nodes;
        auto root=graph.root.as<IGraphicsEffectSource>();
        if(!Walk(root,nodes))return false;
        if(nodes.size()!=4)return false;

        auto const* blur=Find(nodes,CLSID_D2D1GaussianBlur);
        auto const* arithmetic=Find(nodes,CLSID_D2D1ArithmeticComposite);
        auto const* colorMatrix=Find(nodes,CLSID_D2D1ColorMatrix);
        auto const* alphaMask=Find(nodes,CLSID_D2D1AlphaMask);
        if(!blur||!arithmetic||!colorMatrix||!alphaMask)return false;
        if(blur->propertyCount!=3||arithmetic->propertyCount!=2||
            colorMatrix->propertyCount!=3||alphaMask->propertyCount!=0)return false;
        if(blur->sourceCount!=1||arithmetic->sourceCount!=2||
            colorMatrix->sourceCount!=1||alphaMask->sourceCount!=2)return false;

        if(!MappingIs(*blur,L"BlurAmount",0)||!MappingIs(*blur,L"Optimization",1)||
            !MappingIs(*blur,L"BorderMode",2)||
            !MappingIs(*arithmetic,L"Coefficients",0)||!MappingIs(*arithmetic,L"ClampOutput",1)||
            !MappingIs(*colorMatrix,L"ColorMatrix",0)||!MappingIs(*colorMatrix,L"AlphaMode",1)||
            !MappingIs(*colorMatrix,L"ClampOutput",2))return false;

        IPropertyValue value{nullptr};
        if(!GetProperty(*blur,0,value)||value.Type()!=PropertyType::Single||
            std::abs(value.GetSingle()-3.0f)>0.00001f)return false;
        if(!GetProperty(*blur,1,value)||value.Type()!=PropertyType::UInt32||
            value.GetUInt32()!=D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED)return false;
        if(!GetProperty(*blur,2,value)||value.Type()!=PropertyType::UInt32||
            value.GetUInt32()!=D2D1_BORDER_MODE_SOFT)return false;

        if(!GetProperty(*arithmetic,0,value)||value.Type()!=PropertyType::SingleArray)return false;
        com_array<float> coefficients;
        value.GetSingleArray(coefficients);
        if(coefficients.size()!=4||coefficients[0]!=0.0f||coefficients[1]!=1.0f||
            coefficients[2]!=0.0f||coefficients[3]!=0.0f)return false;
        if(!GetProperty(*arithmetic,1,value)||value.Type()!=PropertyType::Boolean||
            value.GetBoolean())return false;

        if(!GetProperty(*colorMatrix,0,value)||value.Type()!=PropertyType::SingleArray)return false;
        com_array<float> matrix;
        value.GetSingleArray(matrix);
        if(matrix.size()!=20)return false;
        for(float component:matrix)if(!std::isfinite(component))return false;
        if(!GetProperty(*colorMatrix,1,value)||value.Type()!=PropertyType::UInt32||
            value.GetUInt32()!=D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED)return false;
        if(!GetProperty(*colorMatrix,2,value)||value.Type()!=PropertyType::Boolean||
            !value.GetBoolean())return false;

        if(graph.animatableProperties.size()!=2||
            graph.animatableProperties[0]!=L"InkBlur.BlurAmount"||
            graph.animatableProperties[1]!=L"InkTint.Coefficients")return false;

        UINT ignoredIndex{};
        Mapping ignoredMapping{};
        if(SUCCEEDED(blur->interop->GetNamedPropertyMapping(L"Unknown",&ignoredIndex,&ignoredMapping)))return false;
        return true;
    }
}

int main()
{
    init_apartment();
    int failures{};
    auto desktop=make_self<SourceStub>().as<IGraphicsEffectSource>();
    auto white=make_self<SourceStub>().as<IGraphicsEffectSource>();
    auto mask=make_self<SourceStub>().as<IGraphicsEffectSource>();
    const auto graph=MakeInkGraph(desktop,white,mask);

    failures+=!Check(ValidateGraph(graph),"live graph source tree, D2D IDs, counts, mappings and boxed property types");
    failures+=!Check(ValidRequestRect(-1024.0f,-1024.0f,512.0f,512.0f),"finite rectangle bounds include clipped off-canvas ink");
    failures+=!Check(!ValidRequestRect(std::numeric_limits<float>::quiet_NaN(),0.0f,12.0f,12.0f),"NaN rectangle rejected");
    failures+=!Check(!ValidRequestRect(0.0f,0.0f,std::numeric_limits<float>::infinity(),12.0f),"infinite rectangle rejected");
    failures+=!Check(!ValidRequestRect(0.0f,0.0f,0.0f,12.0f),"zero-size rectangle rejected");
    failures+=!Check(!ValidRequestRect(1024.0f,0.0f,512.01f,12.0f),"oversized rectangle rejected");

    std::printf("System ink graph CPU checks: %s (%d failures)\n",
        failures==0?"PASS":"FAIL",failures);
    return failures==0?0:1;
}
